using System.Runtime.InteropServices;

namespace AzureGameAssistant.Collector.Interop;

internal static partial class Pdh32
{
    public const uint FmtDouble = 0x00000200;
    public const uint MoreData = 0x800007D2;
    public const uint StatusValidData = 0x00000000;
    public const uint StatusNewData = 0x00000001;

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQueryW(string? dataSource, nuint userData, out IntPtr query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounterW(
        IntPtr query,
        string counterPath,
        nuint userData,
        out IntPtr counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(IntPtr query);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        out uint type,
        out PdhFmtCounterValue value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial uint PdhGetFormattedCounterArrayW(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        out uint itemCount,
        IntPtr buffer);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(IntPtr query);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhRemoveCounter(IntPtr counter);
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct PdhFmtCounterValue
{
    [FieldOffset(0)]
    public uint CStatus;

    [FieldOffset(8)]
    public double DoubleValue;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PdhFmtCounterValueItemW
{
    [FieldOffset(0)]
    public IntPtr Name;

    [FieldOffset(8)]
    public uint CStatus;

    [FieldOffset(16)]
    public double DoubleValue;
}
