using System.Runtime.InteropServices;

namespace AzureGameAssistant.UI.Interop;

internal static partial class NativeMethods
{
    public const int TrayCallbackMessage = 0x8000 + 1;

    public const int WmRButtonUp = 0x0205;
    public const int WmLButtonDblClk = 0x0203;

    public const uint NifMessage = 0x0001;
    public const uint NifIcon = 0x0002;
    public const uint NifTip = 0x0004;
    public const uint NifInfo = 0x0010;

    public const uint NimAdd = 0;
    public const uint NimModify = 1;
    public const uint NimDelete = 2;

    public const int IdiApplication = 32512;

    public const uint NiifInfo = 0x0001;

    public const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point32
    {
        public int X;
        public int Y;
    }

    [LibraryImport("shell32.dll", SetLastError = true, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr LoadIconW(IntPtr instance, IntPtr iconName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point32 point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr window, int command);

    public static unsafe void CopyString(char* destination, int capacity, string? value)
    {
        if (value is null)
        {
            return;
        }

        int length = Math.Min(value.Length, capacity - 1);
        for (int i = 0; i < length; i++)
        {
            destination[i] = value[i];
        }

        destination[length] = '\0';
    }
}
