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
    private const string SessionName = "AzureGameAssistant";
    private const int PollIntervalMs = 500;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan SessionCleanupTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
    ];

    private readonly FileLogger _logger;
    private readonly TelemetryWriter _telemetry;
    private readonly string _executablePath;
    private readonly int _targetPid;
    private readonly Func<bool> _isTargetActive;
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
    private int _emptyAttempts;
    private long _frameCount;
    private volatile FrameCaptureState _state;
    private volatile string? _lastError;
    private volatile bool _restartNeedsCleanup = true;

    public PresentMonSupervisor(
        FileLogger logger,
        TelemetryWriter telemetry,
        string executablePath,
        int targetPid,
        Func<bool>? isTargetActive = null)
    {
        _logger = logger;
        _telemetry = telemetry;
        _executablePath = executablePath;
        _targetPid = targetPid;
        _isTargetActive = isTargetActive ?? (() => true);
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
                    result = new AttemptResult(-1, false, ex.Message, true);
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                // An abnormal exit may have skipped PresentMon's own ETW teardown.
                _restartNeedsCleanup = result.Killed || result.ExitCode != 0;

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

                // A stall is a dead ETW session, not a broken target: keep
                // restarting at the shortest delay instead of giving up.
                // A target that never presents (still loading, minimised, or an
                // API PresentMon cannot see) is not a broken session either, so
                // it retries slowly instead of poisoning the failure budget.
                _emptyAttempts = result.NoFrames ? _emptyAttempts + 1 : 0;
                failures = result.NoFrames || produced > 0 || result.Stalled ? 1 : failures + 1;
                if (failures >= FailureLimit)
                {
                    _lastError = result.Error ?? $"PresentMon 连续 {failures} 次退出（代码 {result.ExitCode}）";
                    _state = FrameCaptureState.Unavailable;
                    _logger.Error(_lastError);
                    return;
                }

                TimeSpan delay = result.NoFrames
                    ? TimeSpan.FromSeconds(Math.Min(15, _emptyAttempts * 2))
                    : RetryDelays[Math.Min(failures - 1, RetryDelays.Length - 1)];
                _lastError = result.Error;
                _state = FrameCaptureState.Starting;
                if (!result.NoFrames || _emptyAttempts <= 2 || _emptyAttempts % 10 == 0)
                {
                    string detail = string.IsNullOrEmpty(result.Error) ? string.Empty : "：" + result.Error;
                    _logger.Warn(
                        $"PresentMon 退出（代码 {result.ExitCode} / 0x{unchecked((uint)result.ExitCode):X8}）{detail}，{delay.TotalSeconds:0.#}s 后重启");
                }

                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _lastError = "帧采集线程异常：" + ex.Message;
            _state = FrameCaptureState.Unavailable;
            _logger.Error(_lastError, ex);
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

    /// <summary>
    /// A killed PresentMon leaves its ETW session behind. PresentMon's
    /// --stop_existing_session path then produces a session that silently stops
    /// delivering events after a few seconds, so clear any orphan with a
    /// dedicated two-phase start first.
    /// </summary>
    private async Task ClearStaleSessionAsync(CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = BuildStartInfo(terminateExisting: true),
        };

        try
        {
            if (!process.Start())
            {
                return;
            }
        }
        catch (Win32Exception ex)
        {
            _logger.Warn("ETW 会话清理进程启动失败：" + ex.Message);
            return;
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warn("ETW 会话清理进程启动失败：" + ex.Message);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(SessionCleanupTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
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
        }
    }

    private async Task<AttemptResult> RunOnceAsync(CancellationToken token)
    {
        // A graceful PresentMon exit stops its own ETW session, so the cleanup
        // pass is only needed after we terminated it ourselves.
        if (_restartNeedsCleanup)
        {
            _restartNeedsCleanup = false;
            await ClearStaleSessionAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                return new AttemptResult(0, false, null, false);
            }
        }

        using var process = new Process
        {
            StartInfo = BuildStartInfo(),
        };

        try
        {
            if (!process.Start())
            {
                return new AttemptResult(-1, false, "无法启动 PresentMon", false);
            }
        }
        catch (Win32Exception ex)
        {
            return new AttemptResult(ex.NativeErrorCode, false, ex.Message, false);
        }
        catch (InvalidOperationException ex)
        {
            return new AttemptResult(-1, false, ex.Message, false);
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
        Task<string?>? pendingLine = null;
        bool gotFrame = false;
        bool killed = false;
        bool stalled = false;
        bool noFrames = false;
        int invalidRun = 0;
        string? error = null;
        long startTicks = Stopwatch.GetTimestamp();
        long lastProgressTicks = startTicks;

        try
        {
            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                pendingLine ??= process.StandardOutput.ReadLineAsync(token).AsTask();
                Task completed = await Task
                    .WhenAny(pendingLine, Task.Delay(PollIntervalMs, token))
                    .ConfigureAwait(false);

                if (completed != pendingLine)
                {
                    double idleMs = ElapsedMs(lastProgressTicks);
                    if (!gotFrame)
                    {
                        // A minimised or unfocused game may legitimately produce
                        // no presents, so only give up while it is actually busy.
                        if (ElapsedMs(startTicks) >= FirstFrameTimeout.TotalMilliseconds &&
                            _isTargetActive())
                        {
                            noFrames = true;
                            error = "PresentMon 启动后 20 秒内未产生帧";
                            break;
                        }
                    }
                    else if (idleMs >= StallTimeout.TotalMilliseconds && _isTargetActive())
                    {
                        stalled = true;
                        error = $"帧采集停顿 {StallTimeout.TotalSeconds:0} 秒（最后一帧后 {idleMs / 1000.0:0.0} 秒无数据）";
                        _logger.Warn($"帧采集停顿：最后一帧后 {idleMs / 1000.0:0.0} 秒无数据，重启 PresentMon");
                        break;
                    }

                    continue;
                }

                string? line = await pendingLine.ConfigureAwait(false);
                pendingLine = null;

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
                    if (gotFrame)
                    {
                        double gapMs = ElapsedMs(lastProgressTicks);
                        if (gapMs >= 2000)
                        {
                            _logger.Info($"帧数据中断 {gapMs / 1000.0:0.0} 秒后自行恢复");
                        }
                    }

                    gotFrame = true;
                    invalidRun = 0;
                    lastProgressTicks = Stopwatch.GetTimestamp();
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
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Flush();
            _process = null;
        }

        if (!process.HasExited && !process.WaitForExit(2000))
        {
            killed = true;

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
            stderr.Contains("Performance Log Users", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("administrative privileges", StringComparison.OrdinalIgnoreCase);

        if (permissionDenied)
        {
            return new AttemptResult(exitCode, true, "需要管理员权限或 Performance Log Users", killed);
        }

        string diagnostics = Compact(stderr);
        if (diagnostics.Length > 0 && (exitCode != 0 || error is not null))
        {
            _logger.Warn("PresentMon 诊断：" + diagnostics);
        }

        if (error is null && exitCode != 0)
        {
            error = diagnostics.Length > 0 ? diagnostics : $"PresentMon 退出代码 {exitCode}";
        }

        return new AttemptResult(exitCode, false, error, killed, stalled, noFrames);
    }

    private ProcessStartInfo BuildStartInfo(bool terminateExisting = false)
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

        info.ArgumentList.Add("--session_name");
        info.ArgumentList.Add(SessionName);

        if (terminateExisting)
        {
            info.ArgumentList.Add("--terminate_existing_session");
            info.ArgumentList.Add("--no_csv");
            return info;
        }

        info.ArgumentList.Add("--process_id");
        info.ArgumentList.Add(_targetPid.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--output_stdout");
        info.ArgumentList.Add("--v2_metrics");
        info.ArgumentList.Add("--terminate_on_proc_exit");
        return info;
    }

    private static double ElapsedMs(long sinceTicks) =>
        (Stopwatch.GetTimestamp() - sinceTicks) * 1000.0 / Stopwatch.Frequency;

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
        catch (Win32Exception)
        {
            // Anti-cheat protected processes can deny handle queries while still running.
            return true;
        }
    }

    private static string Compact(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        int lines = 0;
        foreach (string raw in stderr.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(" | ");
            }

            builder.Append(line);
            if (++lines >= 3 || builder.Length >= 400)
            {
                break;
            }
        }

        if (builder.Length > 500)
        {
            builder.Length = 500;
        }

        return builder.ToString();
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

    private readonly record struct AttemptResult(
        int ExitCode,
        bool PermissionDenied,
        string? Error,
        bool Killed,
        bool Stalled = false,
        bool NoFrames = false);
}
