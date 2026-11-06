using AzureGameAssistant.Collector.Interop;
using AzureGameAssistant.Core.Logging;

namespace AzureGameAssistant.Collector.Sampling;

internal sealed class JitterMonitor : IDisposable
{
    private const int IntervalMs = 16;

    private readonly FileLogger _logger;
    private readonly Thread _thread;
    private readonly long _frequency;

    private volatile bool _stop;
    private int _maxJitterBits;
    public JitterMonitor(FileLogger logger)
    {
        _logger = logger;
        if (!Kernel32.QueryPerformanceFrequency(out _frequency) || _frequency <= 0)
        {
            _frequency = 10_000_000;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "AzureGameAssistant.Jitter",
        };
        _thread.Start();
    }

    public float TakeMaxJitterMs()
    {
        int bits = Interlocked.Exchange(ref _maxJitterBits, 0);
        return BitConverter.Int32BitsToSingle(bits);
    }

    private void Run()
    {
        IntPtr timer = Kernel32.CreateWaitableTimerExW(
            IntPtr.Zero,
            null,
            Kernel32.CreateWaitableTimerHighResolution,
            Kernel32.TimerAllAccess);

        if (timer != IntPtr.Zero)
        {
            long dueTime = -(IntervalMs * 10_000L);
            if (!Kernel32.SetWaitableTimer(timer, ref dueTime, IntervalMs, IntPtr.Zero, IntPtr.Zero, false))
            {
                Kernel32.CloseHandle(timer);
                timer = IntPtr.Zero;
            }
        }

        if (timer == IntPtr.Zero)
        {
            _logger.Info("高精度定时器不可用，抖动测量回退到 Sleep");
        }

        Kernel32.QueryPerformanceCounter(out long previous);

        while (!_stop)
        {
            if (timer != IntPtr.Zero)
            {
                Kernel32.WaitForSingleObject(timer, 100);
            }
            else
            {
                Thread.Sleep(IntervalMs);
            }

            if (_stop)
            {
                break;
            }

            Kernel32.QueryPerformanceCounter(out long now);
            double elapsedMs = (now - previous) * 1000.0 / _frequency;
            previous = now;

            float jitter = (float)Math.Max(0, elapsedMs - IntervalMs);
            if (float.IsFinite(jitter))
            {
                UpdateMax(jitter);
            }
        }

        if (timer != IntPtr.Zero)
        {
            Kernel32.CancelWaitableTimer(timer);
            Kernel32.CloseHandle(timer);
        }
    }

    private void UpdateMax(float jitter)
    {
        int bits = BitConverter.SingleToInt32Bits(jitter);
        while (true)
        {
            int current = Volatile.Read(ref _maxJitterBits);
            if (BitConverter.Int32BitsToSingle(current) >= jitter)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _maxJitterBits, bits, current) == current)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _stop = true;

        if (_thread.IsAlive)
        {
            _thread.Join(1_000);
        }
    }
}
