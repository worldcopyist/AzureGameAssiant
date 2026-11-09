using System.Globalization;
using System.Text;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Core.Diagnostics;

/// <summary>
/// Writes a bounded, crash-tolerant long-run record: periodic CSV rows, a
/// per-30s process ranking, state-change events and a summary on stop. Only the
/// UI process owns an instance; all values come from <see cref="DiagnosticsSnapshot"/>.
/// </summary>
public sealed class LongRunRecorder : IDisposable
{
    private const int ProcessIntervalSeconds = 30;

    private const string SampleHeader =
        "time_utc,uptime_s,sample_age_s,cpu_pct,cpu_freq_pct,gpu_pct,gpu_dedicated_mb,gpu_shared_mb," +
        "mem_used_pct,mem_used_mb,mem_total_mb,mem_available_mb,commit_mb,commit_limit_mb,pagefile_pct," +
        "disk_busy_pct,disk_latency_ms,jitter_ms,fps_inst,fps_avg,fps_1low,fps_01low,frame_time_last_ms," +
        "frames_delta,spikes_delta,frames_fresh,gpu_temp_c,gpu_hotspot_c,cpu_temp_c,vram_used_mb,vram_total_mb," +
        "collector_connected,heartbeat_ms,write_index,collector_pid,collector_error,frame_state,target_pid,frame_error," +
        "ui_ws_mb,ui_private_mb,ui_cpu_pct,ui_handles,top1_name,top1_pid,top1_cpu_pct,top1_gpu_pct,top1_ws_mb," +
        "frame_age_s,frame_write_index";

    private const string ProcessHeader = "time_utc,rank,pid,name,cpu_pct,gpu_pct,working_set_mb";

    private readonly string _directory;
    private readonly int _intervalSeconds;
    private readonly string _contextJson;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly StreamWriter? _samples;
    private readonly StreamWriter? _processes;
    private readonly StreamWriter? _events;

    private readonly List<double> _fps = [];
    private readonly List<double> _lowFps = [];
    private readonly List<double> _uiWorkingSet = [];
    private readonly List<double> _uiCpu = [];
    private readonly Dictionary<string, int> _topOffenders = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _lastRowUtc = DateTime.MinValue;
    private DateTime _lastProcessUtc = DateTime.MinValue;
    private long _lastRowFramesSeen;
    private long _lastRowSpikesSeen;
    private long _spikeTotal;
    private int _rows;
    private int _fpsBelow60;
    private int _fpsBelow30;
    private int _gpuTempRows;
    private int _cpuTempRows;

    private bool _hasLastConnected;
    private bool _lastConnected;
    private int _lastCollectorPid;
    private string _lastFrameState = string.Empty;
    private bool? _lastGpuTemperature;
    private bool? _lastCpuTemperature;

    private bool _gapActive;
    private DateTime _gapStartUtc;
    private int _gapCount;
    private double _gapSecondsTotal;
    private double _gapSecondsMax;

    private int _collectorRestarts;
    private int _frameInterruptions;
    private DateTime? _frameDownSince;
    private double _frameDownSeconds;

    private bool _sawFreshFrames;
    private DateTime? _frameStallSince;
    private int _frameStallCount;
    private double _frameStallSecondsTotal;
    private double _frameStallSecondsMax;

    private readonly HighLoadTracker _highLoads = new();
    private readonly HighLoadMarker[] _highLoadBuffer = new HighLoadMarker[8];
    private long _lastLoadSampleTicks;
    private int _highLoadEvents;
    private DateTime _lastDropEventUtc = DateTime.MinValue;
    private int _dropEvents;

    private bool _writeFailed;
    private bool _stopped;

    public LongRunRecorder(string directory, int intervalSeconds, string contextJson)
    {
        _directory = directory;
        _intervalSeconds = Math.Clamp(intervalSeconds, 1, 3600);
        _contextJson = contextJson;

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "context.json"), contextJson, new UTF8Encoding(false));
        _samples = OpenWriter(Path.Combine(directory, "samples.csv"), SampleHeader);
        _processes = OpenWriter(Path.Combine(directory, "processes.csv"), ProcessHeader);
        _events = OpenWriter(Path.Combine(directory, "events.jsonl"), header: null);
        Event("session_start", new Dictionary<string, object?>
        {
            ["intervalSeconds"] = _intervalSeconds,
        });
    }

    public int IntervalSeconds => _intervalSeconds;

    public string DirectoryPath => _directory;

    public void Record(DiagnosticsSnapshot snapshot)
    {
        if (_stopped)
        {
            return;
        }

        try
        {
            DetectEvents(snapshot);

            if ((snapshot.TimestampUtc - _lastRowUtc).TotalSeconds >= _intervalSeconds &&
                WriteSampleRow(snapshot))
            {
                _lastRowUtc = snapshot.TimestampUtc;
            }

            if ((snapshot.TimestampUtc - _lastProcessUtc).TotalSeconds >= ProcessIntervalSeconds &&
                WriteProcesses(snapshot))
            {
                _lastProcessUtc = snapshot.TimestampUtc;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _writeFailed = true;
        }
    }

    private void DetectEvents(in DiagnosticsSnapshot snapshot)
    {
        if (_hasLastConnected && snapshot.Connected != _lastConnected)
        {
            Event(snapshot.Connected ? "collector_connected" : "collector_disconnected", null);
        }

        if (snapshot.Connected && snapshot.CollectorPid != 0 && snapshot.CollectorPid != _lastCollectorPid)
        {
            bool restart = _lastCollectorPid != 0;
            if (restart)
            {
                _collectorRestarts++;
            }

            Event("collector_pid", new Dictionary<string, object?>
            {
                ["pid"] = snapshot.CollectorPid,
                ["previousPid"] = _lastCollectorPid,
                ["restart"] = restart,
            });
        }

        _hasLastConnected = true;
        _lastConnected = snapshot.Connected;
        if (snapshot.CollectorPid != 0)
        {
            _lastCollectorPid = snapshot.CollectorPid;
        }

        if (!string.Equals(snapshot.FrameState, _lastFrameState, StringComparison.Ordinal))
        {
            bool wasCapturing = _lastFrameState == "capturing";
            bool isCapturing = snapshot.FrameState == "capturing";
            if (wasCapturing && !isCapturing)
            {
                _frameDownSince = snapshot.TimestampUtc;
            }
            else if (!wasCapturing && isCapturing && _frameDownSince is { } downSince)
            {
                _frameInterruptions++;
                _frameDownSeconds += (snapshot.TimestampUtc - downSince).TotalSeconds;
                _frameDownSince = null;
            }

            Event("frame_state", new Dictionary<string, object?>
            {
                ["state"] = snapshot.FrameState,
                ["targetPid"] = snapshot.FrameTargetPid,
                ["error"] = snapshot.FrameError,
            });
            _lastFrameState = snapshot.FrameState;
        }

        if (snapshot.FramesFresh)
        {
            _sawFreshFrames = true;
            double frameMs = snapshot.FrameStats.LastFrameTimeMs;
            double averageFps = snapshot.FrameStats.AverageFps;
            double spikeThreshold = averageFps > 0 ? Math.Max(2_000.0 / averageFps, 50) : 50;
            if (frameMs >= spikeThreshold &&
                (snapshot.TimestampUtc - _lastDropEventUtc).TotalSeconds >= 1.5)
            {
                _lastDropEventUtc = snapshot.TimestampUtc;
                _dropEvents++;
                Event("frame_drop", new Dictionary<string, object?>
                {
                    ["frameTimeMs"] = Math.Round(frameMs, 1),
                    ["averageFps"] = Math.Round(averageFps, 1),
                    ["topProcess"] = snapshot.Processes.Count > 0 ? snapshot.Processes[0].Name : null,
                    ["topPid"] = snapshot.Processes.Count > 0 ? (int)snapshot.Processes[0].Pid : 0,
                });
            }

            if (_frameStallSince is { } stallSince)
            {
                double seconds = Math.Max(0, (snapshot.TimestampUtc - stallSince).TotalSeconds);
                _frameStallSecondsTotal += seconds;
                _frameStallSecondsMax = Math.Max(_frameStallSecondsMax, seconds);
                Event("frame_stall_end", new Dictionary<string, object?>
                {
                    ["seconds"] = Math.Round(seconds, 1),
                });
                _frameStallSince = null;
            }
        }
        else if (_sawFreshFrames &&
                 snapshot.FrameState == "capturing" &&
                 _frameStallSince is null)
        {
            _frameStallSince = snapshot.TimestampUtc;
            _frameStallCount++;
            Event("frame_stall_start", new Dictionary<string, object?>
            {
                ["frameAgeSeconds"] = Math.Round(snapshot.FrameAgeSeconds, 1),
                ["targetPid"] = snapshot.FrameTargetPid,
            });
        }

        bool gpuTemperature = snapshot.HasSample && snapshot.HasGpuTemperature;        if (snapshot.HasSample && (_lastGpuTemperature is null || _lastGpuTemperature != gpuTemperature))
        {
            Event(gpuTemperature ? "gpu_temp_available" : "gpu_temp_missing", null);
            _lastGpuTemperature = gpuTemperature;
        }

        bool cpuTemperature = snapshot.HasSample && snapshot.HasCpuTemperature;
        if (snapshot.HasSample && (_lastCpuTemperature is null || _lastCpuTemperature != cpuTemperature))
        {
            Event(cpuTemperature ? "cpu_temp_available" : "cpu_temp_missing", null);
            _lastCpuTemperature = cpuTemperature;
        }

        if (!snapshot.HasSample)
        {
            return;
        }

        if (snapshot.Sample.UtcTicks != 0 && snapshot.Sample.UtcTicks != _lastLoadSampleTicks)
        {
            _lastLoadSampleTicks = snapshot.Sample.UtcTicks;
            _highLoads.Observe(snapshot.Sample, snapshot.Sample.UtcTicks);
        }

        int completedLoads = _highLoads.DrainCompleted(_highLoadBuffer);
        for (int i = 0; i < completedLoads; i++)
        {
            HighLoadMarker marker = _highLoadBuffer[i];
            _highLoadEvents++;
            Event("high_load", new Dictionary<string, object?>
            {
                ["resource"] = marker.Resource,
                ["peakPct"] = marker.Peak,
                ["seconds"] = marker.Seconds,
                ["topProcess"] = FindProcessName(snapshot, marker.TopPid),
                ["topPid"] = (int)marker.TopPid,
                ["topValue"] = marker.TopValue,
                ["secondProcess"] = FindProcessName(snapshot, marker.SecondPid),
                ["secondPid"] = (int)marker.SecondPid,
            });
        }

        double sampleAge = Math.Max(0, (snapshot.TimestampUtc - new DateTime(snapshot.Sample.UtcTicks, DateTimeKind.Utc)).TotalSeconds);
        double gapThreshold = Math.Max(3.0, snapshot.SamplingIntervalMs / 1000.0 * 2.0);
        if (sampleAge > gapThreshold && !_gapActive)
        {
            _gapActive = true;
            _gapStartUtc = snapshot.TimestampUtc;
            _gapCount++;
            Event("sample_gap_start", new Dictionary<string, object?>
            {
                ["sampleAgeSeconds"] = Math.Round(sampleAge, 2),
            });
        }
        else if (sampleAge <= gapThreshold && _gapActive)
        {
            _gapActive = false;
            double seconds = Math.Max(0, (snapshot.TimestampUtc - _gapStartUtc).TotalSeconds);
            _gapSecondsTotal += seconds;
            _gapSecondsMax = Math.Max(_gapSecondsMax, seconds);
            Event("sample_gap_end", new Dictionary<string, object?>
            {
                ["seconds"] = Math.Round(seconds, 2),
            });
        }
    }

    private static string? FindProcessName(in DiagnosticsSnapshot snapshot, uint pid)
    {
        foreach (ProcessDiagnostic process in snapshot.Processes)
        {
            if (process.Pid == pid)
            {
                return process.Name;
            }
        }

        return null;
    }

    private bool WriteSampleRow(in DiagnosticsSnapshot snapshot)
    {
        if (_samples is null || !snapshot.HasSample)
        {
            return false;
        }

        SampleRecord sample = snapshot.Sample;
        double sampleAge = Math.Max(0, (snapshot.TimestampUtc - new DateTime(sample.UtcTicks, DateTimeKind.Utc)).TotalSeconds);
        double memoryPct = TelemetryMath.MemoryUsagePct(sample);
        SystemMemory.TryGetUsage(sample, out double memUsedMb, out double memTotalMb);
        double fps = snapshot.FramesFresh ? snapshot.FrameStats.InstantFps : 0;
        double fpsAverage = snapshot.FramesFresh ? snapshot.FrameStats.AverageFps : 0;
        long framesDelta = _rows == 0 ? 0 : Math.Max(0, snapshot.FramesSeen - _lastRowFramesSeen);
        long spikesDelta = _rows == 0 ? 0 : Math.Max(0, snapshot.SpikesSeen - _lastRowSpikesSeen);
        _lastRowFramesSeen = snapshot.FramesSeen;
        _lastRowSpikesSeen = snapshot.SpikesSeen;
        _spikeTotal += spikesDelta;

        string topName = string.Empty;
        uint topPid = 0;
        double topCpu = 0;
        double topGpu = 0;
        uint topWs = 0;
        if (snapshot.Processes.Count > 0)
        {
            ProcessDiagnostic top = snapshot.Processes[0];
            topName = Csv(top.Name);
            topPid = top.Pid;
            topCpu = top.CpuPct;
            topGpu = top.GpuPct;
            topWs = top.WorkingSetMb;
            _topOffenders[top.Name] = _topOffenders.TryGetValue(top.Name, out int count) ? count + 1 : 1;
        }

        _rows++;
        if (snapshot.FramesFresh)
        {
            _fps.Add(fpsAverage > 0 ? fpsAverage : fps);
            _lowFps.Add(snapshot.FrameStats.OnePercentLowFps);
            if (fpsAverage > 0 && fpsAverage < 60)
            {
                _fpsBelow60++;
            }

            if (fpsAverage > 0 && fpsAverage < 30)
            {
                _fpsBelow30++;
            }
        }

        if (snapshot.HasSample && snapshot.HasGpuTemperature)
        {
            _gpuTempRows++;
        }

        if (snapshot.HasSample && snapshot.HasCpuTemperature)
        {
            _cpuTempRows++;
        }

        _uiWorkingSet.Add(snapshot.Ui.WorkingSetMb);
        _uiCpu.Add(snapshot.Ui.CpuPct);

        string[] fields =
        [
            Iso(snapshot.TimestampUtc),
            Inv(snapshot.UptimeSeconds, "F0"),
            Inv(sampleAge, "F2"),
            Inv(sample.CpuUtilityPct, "F1"),
            Inv(sample.CpuFrequencyPct, "F0"),
            Inv(sample.GpuUtilPct, "F1"),
            Inv(sample.GpuDedicatedMb, "F0"),
            Inv(sample.GpuSharedMb, "F0"),
            Inv(memoryPct, "F1"),
            Inv(memUsedMb, "F0"),
            Inv(memTotalMb, "F0"),
            Inv(sample.MemAvailableMb, "F0"),
            Inv(sample.MemCommitMb, "F0"),
            Inv(sample.MemCommitLimitMb, "F0"),
            Inv(sample.PageFilePct, "F1"),
            Inv(sample.DiskBusyPct, "F1"),
            Inv(sample.DiskLatencyMs, "F2"),
            Inv(sample.TimerJitterMs, "F1"),
            Inv(fps, "F1"),
            Inv(fpsAverage, "F1"),
            Inv(snapshot.FramesFresh ? snapshot.FrameStats.OnePercentLowFps : 0, "F1"),
            Inv(snapshot.FramesFresh ? snapshot.FrameStats.PointOnePercentLowFps : 0, "F1"),
            Inv(snapshot.FramesFresh ? snapshot.FrameStats.LastFrameTimeMs : 0, "F2"),
            Inv(framesDelta, "F0"),
            Inv(spikesDelta, "F0"),
            snapshot.FramesFresh ? "1" : "0",
            Inv(snapshot.HasGpuTemperature ? snapshot.GpuTemperatureC : 0, "F1"),
            Inv(snapshot.HasGpuTemperature ? snapshot.GpuHotspotC : 0, "F1"),
            Inv(snapshot.HasCpuTemperature ? snapshot.CpuTemperatureC : 0, "F1"),
            Inv(snapshot.VramUsedMb, "F0"),
            Inv(snapshot.VramTotalMb, "F0"),
            snapshot.Connected ? "1" : "0",
            Inv(snapshot.HeartbeatAgeMs, "F0"),
            Inv(snapshot.WriteIndex, "F0"),
            Inv(snapshot.CollectorPid, "F0"),
            Csv(snapshot.CollectorError),
            Csv(snapshot.FrameState),
            Inv(snapshot.FrameTargetPid ?? 0, "F0"),
            Csv(snapshot.FrameError),
            Inv(snapshot.Ui.WorkingSetMb, "F1"),
            Inv(snapshot.Ui.PrivateMb, "F1"),
            Inv(snapshot.Ui.CpuPct, "F1"),
            Inv(snapshot.Ui.Handles, "F0"),
            topName,
            topPid.ToString(CultureInfo.InvariantCulture),
            Inv(topCpu, "F1"),
            Inv(topGpu, "F1"),
            topWs.ToString(CultureInfo.InvariantCulture),
            Inv(snapshot.FrameAgeSeconds, "F2"),
            Inv(snapshot.FrameWriteIndex, "F0"),
        ];

        _samples.WriteLine(string.Join(',', fields));
        _samples.Flush();
        return true;
    }

    private bool WriteProcesses(in DiagnosticsSnapshot snapshot)
    {
        if (_processes is null || snapshot.Processes.Count == 0)
        {
            return false;
        }

        int rank = 1;
        foreach (ProcessDiagnostic process in snapshot.Processes)
        {
            _processes.WriteLine(string.Join(',', new[]
            {
                Iso(snapshot.TimestampUtc),
                rank.ToString(CultureInfo.InvariantCulture),
                process.Pid.ToString(CultureInfo.InvariantCulture),
                Csv(process.Name),
                Inv(process.CpuPct, "F1"),
                Inv(process.GpuPct, "F1"),
                process.WorkingSetMb.ToString(CultureInfo.InvariantCulture),
            }));
            rank++;
        }

        _processes.Flush();
        return true;
    }

    public void Stop(string reason)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        DateTime endedUtc = DateTime.UtcNow;
        if (_gapActive)
        {
            double seconds = Math.Max(0, (endedUtc - _gapStartUtc).TotalSeconds);
            _gapSecondsTotal += seconds;
            _gapSecondsMax = Math.Max(_gapSecondsMax, seconds);
        }

        if (_frameDownSince is { } downSince)
        {
            _frameDownSeconds += (endedUtc - downSince).TotalSeconds;
        }

        if (_frameStallSince is { } stallSince)
        {
            double seconds = Math.Max(0, (endedUtc - stallSince).TotalSeconds);
            _frameStallSecondsTotal += seconds;
            _frameStallSecondsMax = Math.Max(_frameStallSecondsMax, seconds);
        }

        Event("session_stop", new Dictionary<string, object?>
        {
            ["reason"] = reason,
        });

        WriteSummary(endedUtc, reason);

        _samples?.Flush();
        _processes?.Flush();
        _events?.Flush();
        _samples?.Dispose();
        _processes?.Dispose();
        _events?.Dispose();
    }

    private void WriteSummary(DateTime endedUtc, string reason)
    {
        double duration = Math.Max(0, (endedUtc - _startedUtc).TotalSeconds);
        var text = new StringBuilder();
        text.AppendLine("=== 蓝天游戏助手 · 长时诊断摘要 ===");
        text.AppendLine("会话开始(UTC): " + Iso(_startedUtc));
        text.AppendLine("会话结束(UTC): " + Iso(endedUtc));
        text.AppendLine("时长: " + FormatDuration(duration));
        text.AppendLine("停止原因: " + reason);
        text.AppendLine("记录间隔: " + _intervalSeconds + " s");
        text.AppendLine("CSV 行数: " + _rows);
        text.AppendLine("写入失败: " + (_writeFailed ? "是" : "否"));
        text.AppendLine();

        text.AppendLine("[采样连续性]");
        text.AppendLine("数据缺口次数: " + _gapCount);
        text.AppendLine("缺口累计秒数: " + Inv(_gapSecondsTotal, "F1"));
        text.AppendLine("最大缺口秒数: " + Inv(_gapSecondsMax, "F1"));
        double expectedRows = Math.Max(1, duration / Math.Max(1, _intervalSeconds));
        text.AppendLine("记录覆盖率: " + Inv(Math.Min(100, _rows / expectedRows * 100), "F1") + "%");
        text.AppendLine();

        text.AppendLine("[采集与帧链路]");
        text.AppendLine("采集进程重启次数: " + _collectorRestarts);
        text.AppendLine("帧采集中断次数: " + _frameInterruptions);
        text.AppendLine("帧采集不可用累计秒数: " + Inv(_frameDownSeconds, "F1"));
        text.AppendLine("帧率中断次数: " + _frameStallCount);
        text.AppendLine("帧率中断累计秒数: " + Inv(_frameStallSecondsTotal, "F1"));
        text.AppendLine("最长帧率中断秒数: " + Inv(_frameStallSecondsMax, "F1"));
        text.AppendLine("帧尖峰累计(滚动中位数口径): " + _spikeTotal);
        text.AppendLine("掉帧标记数: " + _dropEvents);
        text.AppendLine("硬件高负载事件数: " + _highLoadEvents);
        text.AppendLine("GPU 温度行覆盖: " + Percent(_gpuTempRows, _rows));
        text.AppendLine("CPU 温度行覆盖: " + Percent(_cpuTempRows, _rows));
        text.AppendLine();

        text.AppendLine("[帧率]");
        if (_fps.Count > 0)
        {
            List<double> sorted = _fps.OrderBy(value => value).ToList();
            double mean = sorted.Average();
            double p5 = Percentile(sorted, 0.05);
            double lowMean = _lowFps.Count > 0 ? _lowFps.Average() : 0;
            text.AppendLine("有效帧率行: " + sorted.Count);
            text.AppendLine("平均 FPS: " + Inv(mean, "F1"));
            text.AppendLine("5% 分位 FPS: " + Inv(p5, "F1"));
            text.AppendLine("最低平均 FPS: " + Inv(sorted[0], "F1"));
            text.AppendLine("1% low 均值: " + Inv(lowMean, "F1"));
            text.AppendLine("平均帧率 < 60 的行占比: " + Percent(_fpsBelow60, sorted.Count));
            text.AppendLine("平均帧率 < 30 的行占比: " + Percent(_fpsBelow30, sorted.Count));
        }
        else
        {
            text.AppendLine("无有效帧数据");
        }

        text.AppendLine();
        text.AppendLine("[UI 进程]");
        WriteStats(text, "工作集(MB)", _uiWorkingSet);
        WriteStats(text, "CPU(%)", _uiCpu);

        text.AppendLine();
        text.AppendLine("[高频占用进程（按每个记录行的 CPU 第一名统计）]");
        foreach ((string name, int count) in _topOffenders.OrderByDescending(pair => pair.Value).Take(10))
        {
            text.AppendLine(name + ": " + count + " 次");
        }

        text.AppendLine();
        text.AppendLine("[运行上下文]");
        text.AppendLine(_contextJson);

        try
        {
            File.WriteAllText(Path.Combine(_directory, "summary.txt"), text.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void WriteStats(StringBuilder text, string label, List<double> values)
    {
        if (values.Count == 0)
        {
            text.AppendLine(label + ": 无数据");
            return;
        }

        List<double> sorted = values.OrderBy(value => value).ToList();
        text.AppendLine(label + " 中位数: " + Inv(Percentile(sorted, 0.5), "F1"));
        text.AppendLine(label + " p95: " + Inv(Percentile(sorted, 0.95), "F1"));
        text.AppendLine(label + " 最大: " + Inv(sorted[^1], "F1"));
    }

    private static double Percentile(List<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        int index = Math.Clamp((int)Math.Round(fraction * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }

    private void Event(string type, Dictionary<string, object?>? payload)
    {
        if (_events is null)
        {
            return;
        }

        var line = new StringBuilder(192);
        line.Append("{\"ts\":\"").Append(Iso(DateTime.UtcNow)).Append("\",\"type\":\"").Append(JsonEscape(type)).Append('"');
        if (payload is not null)
        {
            foreach ((string key, object? value) in payload)
            {
                line.Append(",\"").Append(JsonEscape(key)).Append("\":");
                AppendJsonValue(line, value);
            }
        }

        line.Append('}');

        try
        {
            _events.WriteLine(line.ToString());
            _events.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _writeFailed = true;
        }
    }

    private static void AppendJsonValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case bool flag:
                builder.Append(flag ? "true" : "false");
                break;
            case int number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case long number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case double number:
                builder.Append(double.IsFinite(number)
                    ? number.ToString("F3", CultureInfo.InvariantCulture)
                    : "null");
                break;
            default:
                builder.Append('"').Append(JsonEscape(
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)).Append('"');
                break;
        }
    }

    private static string JsonEscape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private static StreamWriter? OpenWriter(string path, string? header)
    {
        try
        {
            var writer = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));
            if (header is not null)
            {
                writer.WriteLine(header);
            }

            writer.Flush();
            return writer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Percent(int part, int total) =>
        total <= 0 ? "0.0%" : Inv(100.0 * part / total, "F1") + "%";

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    private static string Iso(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string Inv(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string Inv(long value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    public void Dispose() => Stop("dispose");
}
