using System.ComponentModel;
using System.Diagnostics;
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
        (string logsDirectory, string issueLogsDirectory) = LogMaintenance.ResolveLogDirectories();
        using var logger = new FileLogger("sensors", logsDirectory, issueDirectory: issueLogsDirectory);
        logger.Enabled = LogMaintenance.IsLoggingEnabled();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.Error("传感器边车未处理异常", e.ExceptionObject as Exception);

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

            using var sidecar = new SensorSidecar(logger, options.CpuMode);
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
            logger.Info($"传感器边车启动：{options.IntervalMs}ms 节拍，CPU 模式={options.CpuMode}，GPU={sidecar.GpuName}，共享内存 {options.MapName}");

            if (options.Once)
            {
                Kernel32.QueryPerformanceCounter(out long qpc);
                SensorRecord sample = sidecar.Read(qpc);
                writer.Write(in sample);
                logger.Info($"单次采样：GPU {Format(sample.GpuTemperatureC)}°C，CPU {Format(sample.CpuTemperatureC)}°C");
                return 0;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.IntervalMs));
            int ticks = 0;
            bool trimmed = false;
            while (WaitForTick(timer, stop.Token))
            {
                Kernel32.QueryPerformanceCounter(out long qpc);
                SensorRecord sample = sidecar.Read(qpc);
                writer.Write(in sample);

                // LibreHardwareMonitor keeps the pages it touched while
                // enumerating every controller. Once the first samples are in,
                // hand the unused ones back so the resident cost stays low.
                if (!trimmed && ++ticks >= 30)
                {
                    trimmed = true;
                    TrimInitialWorkingSet(logger);
                }
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

    private static void TrimInitialWorkingSet(FileLogger logger)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            long before = process.WorkingSet64 / 1048576;
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
            if (!PsApi.EmptyWorkingSet(process.Handle))
            {
                return;
            }

            process.Refresh();
            logger.Info($"传感器边车回收初始化内存：{before} MB → {process.WorkingSet64 / 1048576} MB");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            logger.Warn($"传感器边车回收内存失败：{ex.Message}");
        }
    }

    private static string Format(float value) => float.IsNaN(value) ? "—" : value.ToString("F1", CultureInfo.InvariantCulture);

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            AzureGameAssistant.Sensors — 可选厂商传感器边车

            用法：
              AzureGameAssistant.Sensors [--interval 1000] [--mmf <name>] [--cpu-mode acpi] [--once]

            选项：
              --interval <ms>      采样节拍，500–10000，默认 1000
              --mmf <name>         传感器共享内存名称，默认 Local\AzureGameAssistant.Sensors.v1
              --cpu-mode <mode>    CPU 温度来源：acpi（默认，无需驱动）、driver（启用 LHM CPU 组）、off
              --once               写入一个采样后退出（用于自检）
              --help               显示本帮助

            退出码：0 正常，2 初始化失败，3 检测到内核驱动，4 无可用 GPU。
            """);
    }
}

internal sealed class SidecarOptions
{
    public int IntervalMs { get; private init; } = 1_000;
    public string MapName { get; private init; } = SensorLayout.MapName;
    public string CpuMode { get; private init; } = "acpi";
    public bool Once { get; private init; }
    public bool ShowHelp { get; private init; }
    public string? ParseError { get; private init; }

    public static SidecarOptions Parse(string[] args)
    {
        int interval = 1_000;
        string mapName = SensorLayout.MapName;
        string cpuMode = "acpi";
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

                case "--cpu-mode":
                    if (i + 1 < args.Length)
                    {
                        string mode = args[i + 1].Trim().ToLowerInvariant();
                        if (mode is "acpi" or "driver" or "off")
                        {
                            cpuMode = mode;
                            i++;
                        }
                        else
                        {
                            error = "--cpu-mode 只接受 acpi、driver 或 off";
                        }
                    }
                    else
                    {
                        error = "--cpu-mode 需要一个模式";
                    }

                    break;
            }
        }

        return new SidecarOptions
        {
            IntervalMs = interval,
            MapName = mapName,
            CpuMode = cpuMode,
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

internal static partial class PsApi
{
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyWorkingSet(IntPtr process);
}
