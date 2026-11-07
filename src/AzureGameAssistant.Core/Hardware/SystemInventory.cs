using System.Runtime.InteropServices;
using System.Text;

namespace AzureGameAssistant.Core.Hardware;

/// <summary>
/// Read-only machine description used by AI facts and the MCP server. Only
/// non-identifying values are exposed: OS edition, CPU model, RAM size, GPU
/// model and driver version. Serial numbers, EDID, user and machine names are
/// deliberately never read.
/// </summary>
public sealed record SystemInventorySnapshot(
    string Os,
    string Cpu,
    int LogicalProcessors,
    double MemoryGb,
    IReadOnlyList<string> Gpu,
    string? Driver);

public static class SystemInventory
{
    private const string GpuClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly Lazy<SystemInventorySnapshot> Cached = new(CaptureCore);

    public static SystemInventorySnapshot Capture() => Cached.Value;

    private static SystemInventorySnapshot CaptureCore()
    {
        string product = ReadString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName") ?? "Windows";
        string? displayVersion = ReadString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
        int build = Environment.OSVersion.Version.Build;

        if (build >= 22000 && product.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            product = "Windows 11" + product["Windows 10".Length..];
        }

        string os = string.IsNullOrWhiteSpace(displayVersion) ? product : product + " " + displayVersion;

        string cpu = ReadString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")
                     ?? RuntimeInformation.ProcessArchitecture.ToString();

        double memoryGb = 0;
        if (OperatingSystem.IsWindows() && GetPhysicallyInstalledSystemMemory(out ulong kilobytes))
        {
            memoryGb = Math.Round(kilobytes / 1024.0 / 1024.0, 1);
        }

        var gpu = new List<string>();
        string? driver = null;
        foreach ((string name, string? version) in ReadGpuDevices())
        {
            if (!gpu.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                gpu.Add(name);
            }

            driver ??= version;
        }

        return new SystemInventorySnapshot(
            os.Trim(),
            cpu.Trim(),
            Environment.ProcessorCount,
            memoryGb,
            gpu,
            driver);
    }

    private static IEnumerable<(string Name, string? Version)> ReadGpuDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        IntPtr root = OpenKey(GpuClassKey);
        if (root == IntPtr.Zero)
        {
            yield break;
        }

        try
        {
            var buffer = new StringBuilder(256);
            for (int index = 0; ; index++)
            {
                int length = buffer.Capacity;
                int status = RegEnumKeyExW(root, index, buffer, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (status != 0)
                {
                    yield break;
                }

                string subKey = GpuClassKey + "\\" + buffer.ToString(0, length);
                IntPtr device = OpenKey(subKey);
                if (device == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    string? name = ReadString(device, "DriverDesc");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    yield return (name.Trim(), ReadString(device, "DriverVersion")?.Trim());
                }
                finally
                {
                    RegCloseKey(device);
                }
            }
        }
        finally
        {
            RegCloseKey(root);
        }
    }

    private static IntPtr OpenKey(string path)
    {
        int status = RegOpenKeyExW(HkeyLocalMachine, path, 0, KeyRead, out IntPtr key);
        return status == 0 ? key : IntPtr.Zero;
    }

    private static string? ReadString(string path, string name)
    {
        IntPtr key = OpenKey(path);
        if (key == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ReadString(key, name);
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static string? ReadString(IntPtr key, string name)
    {
        int size = 0;
        int status = RegQueryValueExW(key, name, IntPtr.Zero, out _, null, ref size);
        if (status != 0 || size <= 1 || size > 16_384)
        {
            return null;
        }

        byte[] data = new byte[size];
        status = RegQueryValueExW(key, name, IntPtr.Zero, out _, data, ref size);
        if (status != 0)
        {
            return null;
        }

        return Encoding.Unicode.GetString(data, 0, size).TrimEnd('\0').Trim();
    }

    private static readonly IntPtr HkeyLocalMachine = unchecked((IntPtr)(int)0x80000002);
    private const int KeyRead = 0x20019;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string subKey, int options, int samDesired, out IntPtr result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(
        IntPtr hKey,
        string valueName,
        IntPtr reserved,
        out int type,
        byte[]? data,
        ref int dataSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegEnumKeyExW(
        IntPtr hKey,
        int index,
        StringBuilder name,
        ref int nameLength,
        IntPtr reserved,
        IntPtr className,
        IntPtr classNameLength,
        IntPtr lastWriteTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);
}
