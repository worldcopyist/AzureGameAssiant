using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AzureGameAssistant.Collector.Interop;
using AzureGameAssistant.Collector.Ipc;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Logging;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Collector.Frames;

internal enum FrameCaptureState
{
    Stopped,
    Starting,
    Capturing,
    PermissionDenied,
    Unavailable,
}

/// <summary>
/// Owns the PresentMon child process: argument construction, CSV ingestion into
/// the frame region, restart backoff and shutdown. Only one target PID is
/// supported; when the target exits the supervisor stops instead of restarting.
/// </summary>
internal sealed class PresentMonSupervisor : IDisposable
{
    private const int BatchSize = 64;
    private const int FailureLimit = 3;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
    ];

    private readonly FileLogger _logger;
    private readonly TelemetryWriter _telemetry;
    private readonly string _executablePath;
    private readonly int _targetPid;
    private readonly CancellationTokenSource _stop = new();
    private readonly FrameRecord[] _batch = new FrameRecord[BatchSize];
    private readonly Stopwatch _flushClock = Stopwatch.StartNew();

    private Task? _task;
    private Process? _process;
    private IntPtr _job;
    private long _startQpc;
    private long _lastFrameQpc;
    private long _frequency = 10_000_000;
    private int _batchCount;
    private long _frameCount;
    private volatile FrameCaptureState _state;
    private volatile string? _lastError;

    public PresentMonSupervisor(FileLogger logger, TelemetryWriter telemetry, string executablePath, int targetPid)
    {
        _logger = logger;
        _telemetry = telemetry;
        _executablePath = executablePath;
        _targetPid = targetPid;
    }

    public FrameCaptureState State => _state;

    public int TargetPid => _targetPid;

    public bool IsCapturing => _state == FrameCaptureState.Capturing;

    public string? LastError => _lastError;

    public long FrameCount => Interlocked.Read(ref _frameCount);

    public void Start()
    {
        if (_task is not null)
        {
            return;
        }

        if (!File.Exists(_executablePath))
        {
            _lastError = $"未找到 PresentMon：{_executablePath}";
            _state = FrameCaptureState.Unavailable;
            _logger.Warn(_lastError);
            return;
        }

        if (!PresentMonBinary.Verify(_executablePath, out string actualHash))
        {
            _lastError = string.IsNullOrEmpty(actualHash)
                ? $"PresentMon 无法读取：{_executablePath}"
                : $"PresentMon 校验失败（SHA256 {actualHash}）";
            _state = FrameCaptureState.Unavailable;
            _logger.Warn(_lastError);
            return;
        }

        _job = JobObject.CreateKillOnClose();
        _state = FrameCaptureState.Starting;
        _logger.Info($"帧采集启动：PID {_targetPid}，PresentMon {_executablePath}");
        _task = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    public void Stop()
    {
        _stop.Cancel();

        try
        {
            _task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        try
        {
            Process? process = _process;
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        int failures = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                long before = Interlocked.Read(ref _frameCount);
                AttemptResult result;

                try
                {
                    result = await RunOnceAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"PresentMon 运行异常：{ex.Message}");
                    result = new AttemptResult(-1, false, ex.Message);
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (result.PermissionDenied)
                {
                    _lastError = "需要管理员权限或 Performance Log Users";
                    _state = FrameCaptureState.PermissionDenied;
                    _logger.Warn($"PresentMon 权限不足：{_lastError}");
                    return;
                }

                long produced = Interlocked.Read(ref _frameCount) - before;
                if (!IsTargetAlive())
                {
                    _state = FrameCaptureState.Stopped;
                    _lastError = null;
                    _logger.Info("目标进程已退出，停止帧采集");
                    return;
                }

                failures = produced > 0 ? 1 : failures + 1;
                if (failures >= FailureLimit)
                {
                    _lastError = result.Error ?? $"PresentMon 连续 {failures} 次退出（代码 {result.ExitCode}）";
                    _state = FrameCaptureState.Unavailable;
                    _logger.Error(_lastError);
                    return;
                }

                TimeSpan delay = RetryDelays[Math.Min(failures - 1, RetryDelays.Length - 1)];
                _lastError = result.Error;
                _state = FrameCaptureState.Starting;
                _logger.Warn($"PresentMon 退出（代码 {result.ExitCode}），{delay.TotalSeconds:0}s 后重启");
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Flush();
            if (token.IsCancellationRequested || _state is FrameCaptureState.Starting or FrameCaptureState.Capturing)
            {
                _state = FrameCaptureState.Stopped;
            }
        }
    }

    private async Task<AttemptResult> RunOnceAsync(CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = BuildStartInfo(),
        };

        try
        {
            if (!process.Start())
            {
                return new AttemptResult(-1, false, "无法启动 PresentMon");
            }
        }
        catch (Win32Exception ex)
        {
            return new AttemptResult(ex.NativeErrorCode, false, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new AttemptResult(-1, false, ex.Message);
        }

        _process = process;
        if (_job != IntPtr.Zero && !JobObject.Assign(_job, process))
        {
            _logger.Warn($"PresentMon 未能加入作业对象：{Marshal.GetLastWin32Error()}");
        }

        _state = FrameCaptureState.Starting;
        _lastError = null;

        Kernel32.QueryPerformanceCounter(out _startQpc);
        if (!Kernel32.QueryPerformanceFrequency(out _frequency) || _frequency <= 0)
        {
            _frequency = 10_000_000;
        }

        _lastFrameQpc = _startQpc;

        Task<string> stderrTask = process.StandardError.ReadToEndAsync(token);
        FrameCsvParser? parser = null;
        bool gotFrame = false;
        int invalidRun = 0;
        string? error = null;

        using var firstFrame = CancellationTokenSource.CreateLinkedTokenSource(token);
        firstFrame.CancelAfter(FirstFrameTimeout);

        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput
                        .ReadLineAsync(gotFrame ? token : firstFrame.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (!gotFrame && !token.IsCancellationRequested)
                    {
                        error = "PresentMon 启动后 10 秒内未产生帧";
                    }

                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (parser is null)
                {
                    try
                    {
                        parser = new FrameCsvParser(line);
                    }
                    catch (FormatException ex)
                    {
                        error = ex.Message;
                        break;
                    }

                    continue;
                }

                if (parser.TryParse(line, out PresentMonFrame frame))
                {
                    gotFrame = true;
                    invalidRun = 0;
                    Append(in frame);

                    if (_batchCount >= BatchSize || _flushClock.Elapsed >= FlushInterval)
                    {
                        Flush();
                    }
                }
                else if (++invalidRun >= 100)
                {
                    error = "连续 100 行 CSV 无法解析";
                    break;
                }
            }
        }
        finally
        {
            Flush();
            _process = null;
        }

        if (!process.HasExited && !process.WaitForExit(2000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }

            process.WaitForExit(2000);
        }

        int exitCode = process.HasExited ? process.ExitCode : -1;
        string stderr = string.Empty;

        try
        {
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        bool permissionDenied = exitCode == 6 ||
            stderr.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Performance Log Users", StringComparison.OrdinalIgnoreCase);

        return new AttemptResult(
            exitCode,
            permissionDenied,
            permissionDenied ? "需要管理员权限或 Performance Log Users" : error);
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var info = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        info.ArgumentList.Add("--process_id");
        info.ArgumentList.Add(_targetPid.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--output_stdout");
        info.ArgumentList.Add("--v2_metrics");
        info.ArgumentList.Add("--session_name");
        info.ArgumentList.Add("AzureGameAssistant");
        info.ArgumentList.Add("--terminate_on_proc_exit");
        info.ArgumentList.Add("--stop_existing_session");
        return info;
    }

    private void Append(in PresentMonFrame frame)
    {
        if (_batchCount >= _batch.Length)
        {
            Flush();
        }

        if (frame.TimeMs >= 0)
        {
            _lastFrameQpc = _startQpc + (long)(frame.TimeMs * _frequency / 1000.0);
        }
        else
        {
            _lastFrameQpc += (long)(frame.FrameTimeMs * _frequency / 1000.0);
        }

        _batch[_batchCount++] = new FrameRecord
        {
            QpcTicks = _lastFrameQpc,
            FrameTimeMs = (float)frame.FrameTimeMs,
            CpuBusyMs = (float)frame.CpuBusyMs,
            CpuWaitMs = (float)frame.CpuWaitMs,
            GpuLatencyMs = (float)frame.GpuLatencyMs,
            GpuBusyMs = (float)frame.GpuBusyMs,
            GpuWaitMs = (float)frame.GpuWaitMs,
            DisplayLatencyMs = (float)frame.DisplayLatencyMs,
            AnimationErrorMs = (float)frame.AnimationErrorMs,
            Pid = frame.Pid,
            PresentMode = (ushort)frame.PresentMode,
        };

        Interlocked.Increment(ref _frameCount);
    }

    private void Flush()
    {
        if (_batchCount == 0)
        {
            return;
        }

        _telemetry.WriteFrames(_batch.AsSpan(0, _batchCount));
        _batchCount = 0;
        _flushClock.Restart();

        if (_state != FrameCaptureState.Capturing)
        {
            _state = FrameCaptureState.Capturing;
        }
    }

    private bool IsTargetAlive()
    {
        try
        {
            using Process process = Process.GetProcessById(_targetPid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        Stop();

        if (_job != IntPtr.Zero)
        {
            Kernel32.CloseHandle(_job);
            _job = IntPtr.Zero;
        }

        _stop.Dispose();
    }

    private readonly record struct AttemptResult(int ExitCode, bool PermissionDenied, string? Error);
}
