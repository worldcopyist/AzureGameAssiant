using System.Globalization;
using System.Runtime.InteropServices;
using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Logging;
using AzureGameAssistant.Core.Sensors;

namespace AzureGameAssistant.Sensors;

internal static class Program
{
    private const string MutexName = @"Local\AzureGameAssistant.Sensors";

    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("AzureGameAssistant.Sensors 仅支持 Windows。");
            return 1;
        }

        SidecarOptions options = SidecarOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (options.ParseError is not null)
        {
            Console.Error.WriteLine(options.ParseError);
            return 1;
        }

        AppPaths.EnsureDirectories();
        using var logger = new FileLogger("sensors");

        IntPtr mutex = Kernel32.CreateMutexW(IntPtr.Zero, initialOwner: true, MutexName);
        if (mutex == IntPtr.Zero)
        {
            logger.Error($"创建单实例互斥体失败：{Marshal.GetLastWin32Error()}");
            return 1;
        }

        if (Marshal.GetLastWin32Error() == Kernel32.ErrorAlreadyExists)
        {
            logger.Info("传感器边车已在运行，本次启动退出");
            Kernel32.CloseHandle(mutex);
            return 0;
        }

        try
        {
            string? driver = KernelDriverCheck.FindRunning();
            if (driver is not null)
            {
                logger.Error($"检测到内核驱动 {driver} 正在运行，拒绝初始化传感器");
                return 3;
            }

            using var sidecar = new SensorSidecar(logger);
            if (!sidecar.Open())
            {
                return 2;
            }

            if (!sidecar.HasGpu)
            {
                logger.Warn("未枚举到可用 GPU，传感器边车退出");
                return 4;
            }

            driver = KernelDriverCheck.FindRunning();
            if (driver is not null)
            {
                logger.Error($"LHM 初始化后检测到内核驱动 {driver}，已停止传感器");
                return 3;
            }

            using var writer = new SensorMemoryWriter(options.MapName, options.IntervalMs);
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stop.Cancel();
            };

            sidecar.StartCpuMonitor(stop.Token);
            logger.Info($"传感器边车启动：{options.IntervalMs}ms 节拍，GPU={sidecar.GpuName}，共享内存 {options.MapName}");

            if (options.Once)
            {
                Kernel32.QueryPerformanceCounter(out long qpc);
                SensorRecord sample = sidecar.Read(qpc);
                writer.Write(in sample);
                logger.Info($"单次采样：GPU {Format(sample.GpuTemperatureC)}°C，CPU {Format(sample.CpuTemperatureC)}°C");
                return 0;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.IntervalMs));
            while (WaitForTick(timer, stop.Token))
            {
                Kernel32.QueryPerformanceCounter(out long qpc);
                SensorRecord sample = sidecar.Read(qpc);
                writer.Write(in sample);
            }

            logger.Info("传感器边车停止");
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error("传感器边车异常退出", ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Kernel32.ReleaseMutex(mutex);
            Kernel32.CloseHandle(mutex);
        }
    }

    private static bool WaitForTick(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return timer.WaitForNextTickAsync(token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string Format(float value) => float.IsNaN(value) ? "—" : value.ToString("F1", CultureInfo.InvariantCulture);

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            AzureGameAssistant.Sensors — 可选厂商传感器边车（GPU-only，不加载内核驱动）

            用法：
              AzureGameAssistant.Sensors [--interval 1000] [--mmf <name>] [--once]

            选项：
              --interval <ms>   采样节拍，500–10000，默认 1000
              --mmf <name>      传感器共享内存名称，默认 Local\AzureGameAssistant.Sensors.v1
              --once            写入一个采样后退出（用于自检）
              --help            显示本帮助

            退出码：0 正常，2 初始化失败，3 检测到内核驱动，4 无可用 GPU。
            """);
    }
}

internal sealed class SidecarOptions
{
    public int IntervalMs { get; private init; } = 1_000;
    public string MapName { get; private init; } = SensorLayout.MapName;
    public bool Once { get; private init; }
    public bool ShowHelp { get; private init; }
    public string? ParseError { get; private init; }

    public static SidecarOptions Parse(string[] args)
    {
        int interval = 1_000;
        string mapName = SensorLayout.MapName;
        bool once = false;
        bool help = false;
        string? error = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    help = true;
                    break;

                case "--gpu-only":
                    break;

                case "--once":
                    once = true;
                    break;

                case "--interval":
                    if (i + 1 < args.Length &&
                        int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
                        value is >= 500 and <= 10_000)
                    {
                        interval = value;
                        i++;
                    }
                    else
                    {
                        error = "--interval 需要 500–10000 之间的毫秒数";
                    }

                    break;

                case "--mmf":
                    if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        mapName = args[i + 1];
                        i++;
                    }
                    else
                    {
                        error = "--mmf 需要一个共享内存名称";
                    }

                    break;
            }
        }

        return new SidecarOptions
        {
            IntervalMs = interval,
            MapName = mapName,
            Once = once,
            ShowHelp = help,
            ParseError = error,
        };
    }
}

internal static partial class Kernel32
{
    public const int ErrorAlreadyExists = 183;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateMutexW(IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseMutex(IntPtr mutex);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryPerformanceCounter(out long counter);
}
