using System.Runtime.InteropServices;
using AzureGameAssistant.Collector.Interop;
using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Logging;
using AzureGameAssistant.Core.Settings;

namespace AzureGameAssistant.Collector;

internal static class Program
{
    private const string MutexName = @"Local\AzureGameAssistant.Collector";

    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("AzureGameAssistant.Collector 仅支持 Windows。");
            return 1;
        }

        AppPaths.EnsureDirectories();
        using var logger = new FileLogger("collector");
        logger.Enabled = LogMaintenance.IsLoggingEnabled();

        CollectorOptions options = CollectorOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (options.ParseError is not null)
        {
            logger.Error(options.ParseError);
            Console.Error.WriteLine(options.ParseError);
            return 1;
        }

        IntPtr mutex = Kernel32.CreateMutexW(IntPtr.Zero, initialOwner: true, MutexName);
        if (mutex == IntPtr.Zero)
        {
            logger.Error($"创建单实例互斥体失败：{Marshal.GetLastWin32Error()}");
            return 1;
        }

        if (Marshal.GetLastWin32Error() == Kernel32.ErrorAlreadyExists)
        {
            logger.Info("采集进程已在运行，本次启动退出");
            Console.Error.WriteLine("采集进程已在运行。");
            Kernel32.CloseHandle(mutex);
            return 2;
        }

        try
        {
            SettingsLoadResult settingsResult = new SettingsService().Load();
            if (settingsResult.Warning is not null)
            {
                logger.Warn(settingsResult.Warning);
            }

            using var host = new CollectorHost(settingsResult.Settings, logger, options);
            int exitCode = host.RunAsync(CancellationToken.None).GetAwaiter().GetResult();

            if (options.Duration is not null)
            {
                Console.WriteLine($"samples={host.SampleCount} telemetry={(host.IsTelemetryOpen ? "ok" : "failed")}");
            }

            return exitCode;
        }
        catch (Exception ex)
        {
            logger.Error("采集进程异常退出", ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Kernel32.ReleaseMutex(mutex);
            Kernel32.CloseHandle(mutex);
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AzureGameAssistant.Collector");
        Console.WriteLine("  --duration <seconds>   运行指定秒数后退出（冒烟测试）");
        Console.WriteLine("  --interval <ms>        覆盖采样间隔（100–10000）");
        Console.WriteLine("  --no-pipe              不启动控制管道");
        Console.WriteLine("  --help                 显示本帮助");
    }
}
