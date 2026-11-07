using System.IO;
using System.Security.Principal;
using System.Windows.Media;
using AzureGameAssistant.Core.Diagnostics;
using AzureGameAssistant.Core.Frames;
using Microsoft.Win32;

namespace AzureGameAssistant.UI.Services;

public enum PermissionState
{
    Ok,
    Warning,
    Missing,
}

public sealed record PermissionCheck(string Name, PermissionState State, string Detail)
{
    public Brush StatusBrush => State switch
    {
        PermissionState.Ok => Brushes.LimeGreen,
        PermissionState.Warning => Brushes.Goldenrod,
        _ => Brushes.OrangeRed,
    };

    public string StatusText => State switch
    {
        PermissionState.Ok => "OK",
        PermissionState.Warning => "!",
        _ => "X",
    };
}

public sealed record PermissionSnapshot(
    bool IsElevated,
    bool IsPerformanceLogUser,
    bool TelemetryOpen,
    double HeartbeatAgeMs,
    string FrameState,
    string? FrameError,
    bool GpuSensorsAvailable,
    bool CpuTemperatureAvailable,
    string CpuMode,
    string PresentMonPath);

public static class PermissionDiagnostics
{
    private static readonly SecurityIdentifier PerformanceLogUsersSid =
        new("S-1-5-32-559");

    public static bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool IsPerformanceLogUser()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(PerformanceLogUsersSid);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool IsPawnIoInstalled()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string ResolvePresentMonPath(string configured)
    {
        if (Path.IsPathRooted(configured))
        {
            return Path.GetFullPath(configured);
        }

        string baseDirectory = AppContext.BaseDirectory;
        string separator = Path.DirectorySeparatorChar.ToString();
        string configuration = baseDirectory.Contains(
            $"{separator}Release{separator}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        string[] candidates =
        [
            Path.Combine(baseDirectory, configured),
            Path.Combine(baseDirectory, "collector", configured),
            Path.GetFullPath(Path.Combine(
                baseDirectory,
                "..", "..", "..", "..",
                "AzureGameAssistant.Collector",
                "bin",
                configuration,
                "net8.0",
                configured)),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    public static IReadOnlyList<PermissionCheck> Evaluate(
        PermissionSnapshot snapshot,
        LocalizationService loc)
    {
        var checks = new List<PermissionCheck>();

        checks.Add(snapshot.IsElevated
            ? new PermissionCheck(loc["PermAdminName"], PermissionState.Ok, loc["PermAdminOk"])
            : new PermissionCheck(loc["PermAdminName"], PermissionState.Missing, loc["PermAdminMissing"]));

        checks.Add(snapshot.IsPerformanceLogUser
            ? new PermissionCheck(loc["PermPerfLogName"], PermissionState.Ok, loc["PermPerfLogOk"])
            : new PermissionCheck(loc["PermPerfLogName"], PermissionState.Warning, loc["PermPerfLogMissing"]));

        if (!File.Exists(snapshot.PresentMonPath))
        {
            checks.Add(new PermissionCheck(
                loc["PermPresentMonName"],
                PermissionState.Missing,
                loc.Format("PermPresentMonMissing", snapshot.PresentMonPath)));
        }
        else if (!PresentMonBinary.Verify(snapshot.PresentMonPath, out string actualHash))
        {
            checks.Add(new PermissionCheck(
                loc["PermPresentMonName"],
                PermissionState.Missing,
                loc.Format("PermPresentMonHash", actualHash)));
        }
        else
        {
            checks.Add(new PermissionCheck(loc["PermPresentMonName"], PermissionState.Ok, loc["PermPresentMonOk"]));
        }

        EtwProbeResult etw = EtwPermissionProbe.Probe(out int etwError);
        checks.Add(etw switch
        {
            EtwProbeResult.Allowed => new PermissionCheck(loc["PermEtwName"], PermissionState.Ok, loc["PermEtwOk"]),
            EtwProbeResult.AccessDenied or EtwProbeResult.PrivilegeNotHeld => new PermissionCheck(
                loc["PermEtwName"],
                PermissionState.Missing,
                loc["PermEtwDenied"]),
            _ => new PermissionCheck(
                loc["PermEtwName"],
                PermissionState.Warning,
                loc.Format("PermEtwFailed", etwError)),
        });

        bool telemetryFresh = snapshot.TelemetryOpen && snapshot.HeartbeatAgeMs <= 3000;
        checks.Add(telemetryFresh
            ? new PermissionCheck(loc["PermTelemetryName"], PermissionState.Ok, loc["PermTelemetryOk"])
            : new PermissionCheck(loc["PermTelemetryName"], PermissionState.Missing, loc["PermTelemetryMissing"]));

        bool capturing = string.Equals(snapshot.FrameState, "capturing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.FrameState, "starting", StringComparison.OrdinalIgnoreCase);
        if (capturing)
        {
            checks.Add(new PermissionCheck(loc["PermFramesName"], PermissionState.Ok, loc["PermFramesOk"]));
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.FrameError))
        {
            checks.Add(new PermissionCheck(
                loc["PermFramesName"],
                PermissionState.Missing,
                loc.Format("PermFramesError", snapshot.FrameError)));
        }
        else
        {
            checks.Add(new PermissionCheck(loc["PermFramesName"], PermissionState.Warning, loc["PermFramesIdle"]));
        }

        checks.Add(snapshot.GpuSensorsAvailable
            ? new PermissionCheck(loc["PermGpuSensorsName"], PermissionState.Ok, loc["PermGpuSensorsOk"])
            : new PermissionCheck(loc["PermGpuSensorsName"], PermissionState.Warning, loc["PermGpuSensorsMissing"]));

        bool driverMode = string.Equals(snapshot.CpuMode, "driver", StringComparison.OrdinalIgnoreCase);
        bool driverInstalled = IsPawnIoInstalled();

        if (snapshot.CpuTemperatureAvailable)
        {
            checks.Add(new PermissionCheck(loc["PermCpuTempName"], PermissionState.Ok, loc["PermCpuTempOk"]));
        }
        else if (string.Equals(snapshot.CpuMode, "off", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new PermissionCheck(loc["PermCpuTempName"], PermissionState.Warning, loc["PermCpuTempOff"]));
        }
        else if (driverMode && driverInstalled)
        {
            checks.Add(new PermissionCheck(loc["PermCpuTempName"], PermissionState.Warning, loc["PermCpuTempDriverBlocked"]));
        }
        else if (driverMode)
        {
            checks.Add(new PermissionCheck(
                loc["PermCpuTempName"],
                PermissionState.Missing,
                loc["PermCpuTempDriverNotInstalled"]));
        }
        else
        {
            checks.Add(new PermissionCheck(loc["PermCpuTempName"], PermissionState.Missing, loc["PermCpuTempDriverMissing"]));
        }

        return checks;
    }
}
