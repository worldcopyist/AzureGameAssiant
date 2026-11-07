using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Hardware;
using AzureGameAssistant.Core.Ipc;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Sensors;

namespace AzureGameAssistant.Mcp;

public sealed class ToolException(string message) : Exception(message);

/// <summary>
/// Read-only access to the collector and sensor shared memory. Every tool is
/// side-effect free: no control commands except the explicit status query.
/// </summary>
public sealed class HardwareInspector : IDisposable
{
    private readonly TelemetryReader _telemetry = new();
    private readonly SensorReader _sensors = new();
    private readonly ProcessNameCache _names = new();
    private SampleRecord[] _samples = new SampleRecord[TelemetryLayout.SampleCapacity];

    public JsonObject GetSnapshot()
    {
        EnsureTelemetry(out SharedHeader header, out SampleRecord sample);
        SystemInventorySnapshot inventory = SystemInventory.Capture();
        SensorRecord? sensor = ReadSensor();

        var json = new JsonObject
        {
            ["timestampUtc"] = new DateTime(sample.UtcTicks, DateTimeKind.Utc).ToString("O"),
            ["collector"] = new JsonObject
            {
                ["pid"] = header.CollectorPid,
                ["samplingIntervalMs"] = header.SamplingIntervalMs,
                ["heartbeatAgeMs"] = Round(_telemetry.HeartbeatAgeMs),
                ["paused"] = (header.ModeFlags & CollectorModeFlags.Paused) != 0,
                ["deepMode"] = (header.ModeFlags & CollectorModeFlags.DeepMode) != 0,
                ["gameMode"] = (header.ModeFlags & CollectorModeFlags.GameMode) != 0,
                ["sampleCount"] = _telemetry.WriteIndex,
                ["capabilities"] = new JsonObject
                {
                    ["frames"] = (header.CapabilityFlags & CapabilityFlags.Frames) != 0,
                    ["vendorSensors"] = (header.CapabilityFlags & CapabilityFlags.VendorSensors) != 0,
                    ["processAccess"] = (header.CapabilityFlags & CapabilityFlags.ProcessAccess) != 0,
                },
            },
            ["system"] = new JsonObject
            {
                ["os"] = inventory.Os,
                ["cpu"] = inventory.Cpu,
                ["logicalProcessors"] = inventory.LogicalProcessors,
                ["memoryGb"] = inventory.MemoryGb > 0 ? inventory.MemoryGb : null,
                ["gpu"] = GpuArray(inventory.Gpu),
                ["driver"] = inventory.Driver,
            },
            ["cpu"] = new JsonObject
            {
                ["usagePct"] = Round(sample.CpuUtilityPct),
                ["frequencyPct"] = Round(sample.CpuFrequencyPct),
                ["processorQueueLength"] = sample.ProcessorQueueLength,
                ["temperatureC"] = (sample.Flags & SampleFlags.CpuSensorValid) != 0 ? Positive(sample.C0.TemperatureC) : null,
                ["packagePowerW"] = (sample.Flags & SampleFlags.CpuSensorValid) != 0 ? Positive(sample.C0.PackagePowerW) : null,
            },
            ["gpu"] = new JsonObject
            {
                ["usagePct"] = Round(sample.GpuUtilPct),
                ["dedicatedMb"] = sample.GpuDedicatedMb,
                ["sharedMb"] = sample.GpuSharedMb,
                ["temperatureC"] = (sample.Flags & SampleFlags.VendorValid) != 0 ? Positive(sample.V0.TemperatureC) : null,
                ["hotSpotC"] = (sample.Flags & SampleFlags.VendorValid) != 0 ? Positive(sample.V0.HotSpotC) : null,
                ["powerW"] = (sample.Flags & SampleFlags.VendorValid) != 0 ? Positive(sample.V0.PowerW) : null,
                ["coreClockMhz"] = (sample.Flags & SampleFlags.VendorValid) != 0 ? Positive(sample.V0.CoreClockMhz) : null,
                ["memoryClockMhz"] = (sample.Flags & SampleFlags.VendorValid) != 0 ? Positive(sample.V0.MemoryClockMhz) : null,
                ["fanPct"] = (sample.Flags & SampleFlags.VendorValid) != 0 ? Positive(sample.V0.FanPct) : null,
                ["vramUsedMb"] = (sample.Flags & SampleFlags.VendorValid) != 0 ? Round(sample.V0.VramUsedMb) : null,
                ["vramTotalMb"] = (sample.Flags & SampleFlags.VendorValid) != 0 ? Round(sample.V0.VramTotalMb) : null,
            },
            ["memory"] = new JsonObject
            {
                ["totalMb"] = Round(SystemMemory.TotalPhysicalMb),
                ["usedMb"] = Round(Math.Max(0, SystemMemory.TotalPhysicalMb - sample.MemAvailableMb)),
                ["availableMb"] = sample.MemAvailableMb,
                ["commitMb"] = sample.MemCommitMb,
                ["commitLimitMb"] = sample.MemCommitLimitMb,
                ["usagePct"] = Round(TelemetryMath.MemoryUsagePct(sample)),
                ["pageFilePct"] = Round(sample.PageFilePct),
            },
            ["disk"] = new JsonObject
            {
                ["busyPct"] = Round(sample.DiskBusyPct),
                ["latencyMs"] = Round(sample.DiskLatencyMs),
            },
            ["timerJitterMs"] = Round(sample.TimerJitterMs),
            ["foregroundPid"] = sample.ForegroundPid == 0 ? null : sample.ForegroundPid,
            ["processes"] = ProcessArray(sample, 5),
            ["frames"] = FrameSnapshot(sample),
            ["sensors"] = new JsonObject
            {
                ["mapOpen"] = _sensors.IsOpen,
                ["heartbeatAgeMs"] = Round(_sensors.HeartbeatAgeMs),
                ["cpuTemperatureC"] = sensor is { } s && (s.Flags & SensorFlags.CpuValid) != 0 ? Positive(s.CpuTemperatureC) : null,
                ["cpuPowerW"] = sensor is { } s2 && (s2.Flags & SensorFlags.CpuValid) != 0 ? Positive(s2.CpuPowerW) : null,
                ["gpuTemperatureC"] = sensor is { } s3 && (s3.Flags & SensorFlags.GpuValid) != 0 ? Positive(s3.GpuTemperatureC) : null,
                ["gpuHotSpotC"] = sensor is { } s4 && (s4.Flags & SensorFlags.GpuValid) != 0 ? Positive(s4.GpuHotSpotC) : null,
            },
        };

        return json;
    }

    public JsonObject GetHistory(int seconds, int maxPoints, IReadOnlyList<string>? metrics)
    {
        EnsureTelemetry(out _, out SampleRecord latest);
        int count = _telemetry.ReadSamples(_samples);
        if (count <= 0)
        {
            throw new ToolException("遥测共享内存暂时没有样本");
        }

        long cutoff = latest.UtcTicks - (long)seconds * TimeSpan.TicksPerSecond;
        int start = 0;
        while (start < count && _samples[start].UtcTicks < cutoff)
        {
            start++;
        }

        int available = count - start;
        if (available <= 0)
        {
            throw new ToolException("所选时间窗口内没有样本");
        }

        int buckets = Math.Clamp(Math.Min(maxPoints, available), 1, 900);
        var include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (metrics is null || metrics.Count == 0)
        {
            include.UnionWith(["cpu", "gpu", "memory", "vram", "disk", "jitter"]);
        }
        else
        {
            foreach (string metric in metrics)
            {
                string normalized = metric.Trim().ToLowerInvariant();
                if (normalized is "cpu" or "gpu" or "memory" or "vram" or "disk" or "jitter")
                {
                    include.Add(normalized);
                }
            }

            if (include.Count == 0)
            {
                throw new ToolException("metrics 仅支持 cpu/gpu/memory/vram/disk/jitter");
            }
        }

        var array = new JsonArray();
        for (int bucket = 0; bucket < buckets; bucket++)
        {
            int from = start + (int)((long)bucket * available / buckets);
            int to = start + (int)((long)(bucket + 1) * available / buckets);
            if (to <= from)
            {
                continue;
            }

            double cpuSum = 0, cpuMax = 0, gpuSum = 0, gpuMax = 0;
            double memSum = 0, memMin = double.MaxValue, vramMax = 0;
            double diskLatencyMax = 0, diskBusyMax = 0, jitterMax = 0;

            for (int i = from; i < to; i++)
            {
                SampleRecord sample = _samples[i];
                cpuSum += sample.CpuUtilityPct;
                cpuMax = Math.Max(cpuMax, sample.CpuUtilityPct);
                gpuSum += sample.GpuUtilPct;
                gpuMax = Math.Max(gpuMax, sample.GpuUtilPct);
                memSum += TelemetryMath.MemoryUsagePct(sample);
                memMin = Math.Min(memMin, sample.MemAvailableMb);
                diskLatencyMax = Math.Max(diskLatencyMax, sample.DiskLatencyMs);
                diskBusyMax = Math.Max(diskBusyMax, sample.DiskBusyPct);
                jitterMax = Math.Max(jitterMax, sample.TimerJitterMs);
                if ((sample.Flags & SampleFlags.VendorValid) != 0)
                {
                    vramMax = Math.Max(vramMax, sample.V0.VramUsedMb);
                }
            }

            int length = to - from;
            var bucketJson = new JsonObject
            {
                ["t"] = new DateTime(_samples[from].UtcTicks, DateTimeKind.Utc).ToString("O"),
            };

            if (include.Contains("cpu"))
            {
                bucketJson["cpuAvgPct"] = Round(cpuSum / length);
                bucketJson["cpuMaxPct"] = Round(cpuMax);
            }

            if (include.Contains("gpu"))
            {
                bucketJson["gpuAvgPct"] = Round(gpuSum / length);
                bucketJson["gpuMaxPct"] = Round(gpuMax);
            }

            if (include.Contains("memory"))
            {
                bucketJson["memUsageAvgPct"] = Round(memSum / length);
                bucketJson["memAvailableMinMb"] = Round(memMin);
            }

            if (include.Contains("vram"))
            {
                bucketJson["vramUsedMaxMb"] = Round(vramMax);
            }

            if (include.Contains("disk"))
            {
                bucketJson["diskLatencyMaxMs"] = Round(diskLatencyMax);
                bucketJson["diskBusyMaxPct"] = Round(diskBusyMax);
            }

            if (include.Contains("jitter"))
            {
                bucketJson["timerJitterMaxMs"] = Round(jitterMax);
            }

            array.Add(bucketJson);
        }

        return new JsonObject
        {
            ["startUtc"] = new DateTime(_samples[start].UtcTicks, DateTimeKind.Utc).ToString("O"),
            ["endUtc"] = new DateTime(_samples[count - 1].UtcTicks, DateTimeKind.Utc).ToString("O"),
            ["sampleCount"] = available,
            ["bucketSeconds"] = Math.Max(0.1, Math.Round(seconds / (double)buckets, 2)),
            ["samples"] = array,
        };
    }

    public JsonObject GetFrameStats(int seconds)
    {
        EnsureTelemetry(out _, out SampleRecord latest);
        long index = _telemetry.FrameWriteIndex;
        if (index <= 0)
        {
            throw new ToolException("帧采集尚未开始（帧区为空）");
        }

        int capacity = Math.Clamp(seconds * 240, 2_048, TelemetryLayout.FrameCapacity);
        var frames = new FrameRecord[capacity];
        int count = _telemetry.ReadFrames(frames);
        if (count == 0)
        {
            throw new ToolException("无法读取帧数据");
        }

        long frequency = Stopwatch.Frequency;
        long anchorQpc = latest.QpcTicks;
        double windowMs = seconds * 1000.0;
        double sampleAgeMs = (DateTime.UtcNow.Ticks - latest.UtcTicks) / (double)TimeSpan.TicksPerMillisecond;

        var stats = new FrameStats(count);
        var times = new List<double>(count);
        double cpuWait = 0;
        double gpuWait = 0;
        double newestAgeMs = double.PositiveInfinity;
        int displayGaps = 0;
        var pids = new HashSet<int>();

        for (int i = 0; i < count; i++)
        {
            FrameRecord frame = frames[i];
            double frameAgeMs = sampleAgeMs - ((frame.QpcTicks - anchorQpc) * 1000.0 / frequency);
            if (frameAgeMs < -1_000 || frameAgeMs > windowMs)
            {
                continue;
            }

            if (!(frame.FrameTimeMs > 0) || float.IsInfinity(frame.FrameTimeMs))
            {
                continue;
            }

            stats.Add(frame.FrameTimeMs);
            times.Add(frame.FrameTimeMs);
            cpuWait += frame.CpuWaitMs;
            gpuWait += frame.GpuWaitMs;
            newestAgeMs = Math.Min(newestAgeMs, frameAgeMs);
            if (frame.FrameTimeMs > 100)
            {
                displayGaps++;
            }

            if (frame.Pid != 0)
            {
                pids.Add(frame.Pid);
            }
        }

        if (times.Count == 0)
        {
            throw new ToolException("所选窗口内没有有效帧");
        }

        FrameStatsSnapshot snapshot = stats.Snapshot();
        times.Sort();
        double p99 = times[Math.Clamp((int)Math.Ceiling(times.Count * 0.99) - 1, 0, times.Count - 1)];

        return new JsonObject
        {
            ["windowSeconds"] = seconds,
            ["frameCount"] = times.Count,
            ["instantFps"] = Round(snapshot.InstantFps),
            ["avgFps"] = Round(snapshot.AverageFps),
            ["onePercentLowFps"] = Round(snapshot.OnePercentLowFps),
            ["pointOnePercentLowFps"] = Round(snapshot.PointOnePercentLowFps),
            ["lastFrameTimeMs"] = Round(snapshot.LastFrameTimeMs),
            ["maxFrameTimeMs"] = Round(times[^1]),
            ["p99FrameTimeMs"] = Round(p99),
            ["avgCpuWaitMs"] = Round(cpuWait / times.Count),
            ["avgGpuWaitMs"] = Round(gpuWait / times.Count),
            ["displayGapCount"] = displayGaps,
            ["lastFrameAgeMs"] = double.IsPositiveInfinity(newestAgeMs)
                ? null
                : Round(Math.Max(0, newestAgeMs)),
            ["pids"] = PidArray(pids),
        };
    }

    public JsonObject GetProcesses(int limit)
    {
        EnsureTelemetry(out _, out SampleRecord sample);
        return new JsonObject
        {
            ["timestampUtc"] = new DateTime(sample.UtcTicks, DateTimeKind.Utc).ToString("O"),
            ["foregroundPid"] = sample.ForegroundPid == 0 ? null : sample.ForegroundPid,
            ["note"] = "采集进程每拍维护 5 个进程槽；需要完整进程列表请使用任务管理器",
            ["processes"] = ProcessArray(sample, Math.Clamp(limit, 1, 5)),
        };
    }

    public JsonObject GetDiagnostics()
    {
        if (!_telemetry.IsOpen)
        {
            _telemetry.TryOpen();
        }

        if (!_sensors.IsOpen)
        {
            _sensors.TryOpen();
        }

        bool telemetryOpen = _telemetry.IsOpen;
        bool sensorOpen = _sensors.IsOpen;
        bool headerValid = false;
        string? telemetryError = _telemetry.LastOpenError;
        string? sensorError = _sensors.LastOpenError;

        var json = new JsonObject
        {
            ["telemetry"] = new JsonObject
            {
                ["mapName"] = TelemetryLayout.MapName,
                ["open"] = telemetryOpen,
                ["headerValid"] = false,
                ["error"] = telemetryError,
            },
            ["sensors"] = new JsonObject
            {
                ["mapName"] = SensorLayout.MapName,
                ["open"] = sensorOpen,
                ["heartbeatAgeMs"] = Round(_sensors.HeartbeatAgeMs),
                ["error"] = sensorError,
            },
        };

        if (telemetryOpen && _telemetry.TryReadHeader(out SharedHeader header))
        {
            headerValid = header.Magic == TelemetryLayout.Magic &&
                          header.ProtocolVersion == TelemetryLayout.ProtocolVersion;
            var telemetry = (JsonObject)json["telemetry"]!;
            telemetry["headerValid"] = headerValid;
            telemetry["collectorPid"] = header.CollectorPid;
            telemetry["samplingIntervalMs"] = header.SamplingIntervalMs;
            telemetry["heartbeatAgeMs"] = Round(_telemetry.HeartbeatAgeMs);
            telemetry["sampleWriteIndex"] = (double)_telemetry.WriteIndex;
            telemetry["frameWriteIndex"] = (double)_telemetry.FrameWriteIndex;
            telemetry["modeFlags"] = header.ModeFlags;
            telemetry["capabilityFlags"] = header.CapabilityFlags;
        }

        return json;
    }

    public JsonObject GetHeaderStatus()
    {
        EnsureTelemetry(out SharedHeader header, out _);

        return new JsonObject
        {
            ["source"] = "shared-memory",
            ["collectorPid"] = header.CollectorPid,
            ["samplingIntervalMs"] = header.SamplingIntervalMs,
            ["heartbeatAgeMs"] = Round(_telemetry.HeartbeatAgeMs),
            ["paused"] = (header.ModeFlags & CollectorModeFlags.Paused) != 0,
            ["deepMode"] = (header.ModeFlags & CollectorModeFlags.DeepMode) != 0,
            ["gameMode"] = (header.ModeFlags & CollectorModeFlags.GameMode) != 0,
            ["frameCapture"] = (header.ModeFlags & CollectorModeFlags.FrameCapture) != 0,
            ["sensors"] = (header.ModeFlags & CollectorModeFlags.Sensors) != 0,
            ["sampleCount"] = _telemetry.WriteIndex,
            ["frameCount"] = _telemetry.FrameWriteIndex,
            ["capabilities"] = new JsonObject
            {
                ["frames"] = (header.CapabilityFlags & CapabilityFlags.Frames) != 0,
                ["vendorSensors"] = (header.CapabilityFlags & CapabilityFlags.VendorSensors) != 0,
                ["processAccess"] = (header.CapabilityFlags & CapabilityFlags.ProcessAccess) != 0,
            },
            ["uptimeSeconds"] = null,
            ["lastError"] = null,
            ["note"] = "控制管道不可用，以上状态来自共享内存；运行时长与最近错误需要在管理员权限下查询",
        };
    }

    public async Task<JsonObject> GetCollectorStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", ControlProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

            byte[] payload = Encoding.UTF8.GetBytes("{\"id\":1,\"cmd\":\"status\"}");
            byte[] header = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

            byte[] lengthBuffer = new byte[4];
            await ReadExactAsync(pipe, lengthBuffer, cancellationToken).ConfigureAwait(false);
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (length <= 0 || length > ControlProtocol.MaxFrameBytes)
            {
                throw new ToolException("控制管道返回了无效长度");
            }

            byte[] response = new byte[length];
            await ReadExactAsync(pipe, response, cancellationToken).ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(response);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean() ||
                !root.TryGetProperty("data", out JsonElement data))
            {
                throw new ToolException("采集进程返回错误");
            }

            var status = JsonNode.Parse(data.GetRawText()) as JsonObject ?? new JsonObject();
            status["source"] = "control-pipe";
            return status;
        }
        catch (TimeoutException)
        {
            throw new ToolException("采集进程未运行或控制管道无响应");
        }
        catch (UnauthorizedAccessException)
        {
            throw new ToolException(
                "控制管道拒绝访问：MCP 服务需要与采集进程相同的管理员权限才能查询完整状态");
        }
        catch (IOException ex)
        {
            throw new ToolException("无法连接采集进程：" + ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw new ToolException("查询采集状态超时");
        }
    }

    private JsonObject FrameSnapshot(in SampleRecord sample)
    {
        long index = _telemetry.FrameWriteIndex;
        if (index <= 0 || !_telemetry.TryReadLatestFrame(out FrameRecord frame))
        {
            return new JsonObject
            {
                ["capturing"] = false,
                ["frameCount"] = index,
            };
        }

        double ageMs = (DateTime.UtcNow.Ticks - sample.UtcTicks) / (double)TimeSpan.TicksPerMillisecond +
                       (frame.QpcTicks - sample.QpcTicks) * 1000.0 / Stopwatch.Frequency;

        return new JsonObject
        {
            ["capturing"] = ageMs is >= -1_000 and <= 3_000,
            ["frameCount"] = index,
            ["lastFrameTimeMs"] = Positive(frame.FrameTimeMs),
            ["instantFps"] = frame.FrameTimeMs > 0 ? Round(1000.0 / frame.FrameTimeMs) : null,
            ["cpuBusyMs"] = Positive(frame.CpuBusyMs),
            ["cpuWaitMs"] = Positive(frame.CpuWaitMs),
            ["gpuBusyMs"] = Positive(frame.GpuBusyMs),
            ["gpuWaitMs"] = Positive(frame.GpuWaitMs),
            ["pid"] = frame.Pid == 0 ? null : frame.Pid,
        };
    }

    private JsonArray ProcessArray(in SampleRecord sample, int limit)
    {
        var array = new JsonArray();
        ProcessSlot[] slots = [sample.P0, sample.P1, sample.P2, sample.P3, sample.P4];

        foreach (ProcessSlot slot in slots)
        {
            if (array.Count >= limit)
            {
                break;
            }

            if (slot.Pid == 0)
            {
                continue;
            }

            array.Add(new JsonObject
            {
                ["pid"] = slot.Pid,
                ["name"] = _names.GetName(slot.Pid),
                ["cpuPct"] = Round(slot.CpuPct),
                ["gpuPct"] = Round(slot.GpuPct),
                ["workingSetMb"] = slot.WorkingSetMb,
                ["privateMb"] = slot.PrivateMb,
                ["foreground"] = slot.Pid == sample.ForegroundPid || (slot.Flags & ProcessSlotFlags.Foreground) != 0,
                ["protected"] = (slot.Flags & ProcessSlotFlags.Protected) != 0,
            });
        }

        return array;
    }

    private void EnsureTelemetry(out SharedHeader header, out SampleRecord sample)
    {
        header = default;
        sample = default;

        if (!_telemetry.IsOpen || !_telemetry.IsHeaderValid)
        {
            _telemetry.TryOpen();
        }

        if (!_telemetry.TryReadHeader(out header))
        {
            throw new ToolException("遥测共享内存不可用：" + (_telemetry.LastOpenError ?? "采集进程可能未启动"));
        }

        if (header.Magic != TelemetryLayout.Magic || header.ProtocolVersion != TelemetryLayout.ProtocolVersion)
        {
            throw new ToolException("遥测协议版本不匹配，请重启采集进程");
        }

        if (!_telemetry.TryReadLatest(out sample))
        {
            throw new ToolException("采集进程尚未写入样本");
        }
    }

    private SensorRecord? ReadSensor()
    {
        if (!_sensors.IsOpen)
        {
            _sensors.TryOpen();
        }

        return _sensors.Refresh() ? _sensors.Latest : null;
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("控制管道已关闭");
            }

            offset += read;
        }
    }

    private static JsonArray GpuArray(IReadOnlyList<string> gpu)
    {
        var array = new JsonArray();
        foreach (string name in gpu)
        {
            array.Add(JsonValue.Create(name));
        }

        return array;
    }

    private static JsonArray PidArray(IEnumerable<int> pids)
    {
        var array = new JsonArray();
        foreach (int pid in pids)
        {
            array.Add(JsonValue.Create(pid));
        }

        return array;
    }

    private static JsonNode? Round(double value) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? null
            : JsonValue.Create(Math.Round(value, 1));

    private static JsonNode? Round(float value) =>
        float.IsNaN(value) || float.IsInfinity(value)
            ? null
            : JsonValue.Create(Math.Round(value, 1));

    private static JsonNode? Positive(float value) =>
        value > 0 && !float.IsNaN(value) && !float.IsInfinity(value)
            ? JsonValue.Create(Math.Round(value, 1))
            : null;

    public void Dispose()
    {
        _telemetry.Dispose();
        _sensors.Dispose();
    }

    private sealed class ProcessNameCache
    {
        private readonly Dictionary<uint, (string Name, DateTime At)> _cache = new();

        public string GetName(uint pid)
        {
            DateTime now = DateTime.UtcNow;
            if (_cache.TryGetValue(pid, out (string Name, DateTime At) entry) &&
                now - entry.At < TimeSpan.FromSeconds(2))
            {
                return entry.Name;
            }

            string name;
            try
            {
                using Process process = Process.GetProcessById((int)pid);
                name = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                name = string.Empty;
            }

            if (_cache.Count > 512)
            {
                _cache.Clear();
            }

            _cache[pid] = (name, now);
            return name;
        }
    }
}
