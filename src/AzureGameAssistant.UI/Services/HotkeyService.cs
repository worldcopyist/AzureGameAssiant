using System.Runtime.InteropServices;
using System.Windows.Interop;
using AzureGameAssistant.UI.Interop;

namespace AzureGameAssistant.UI.Services;

/// <summary>
/// Registers a single global hotkey against the main window handle. The window
/// stays hidden in the tray, so the hotkey keeps working while gaming.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public const string DefaultGesture = "Ctrl+Alt+M";
    public const string GestureFormatHint = "Ctrl+Alt+M";

    private const int HotkeyId = 0xA71;

    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    public HotkeyService(IntPtr windowHandle)
    {
        _source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException("主窗口句柄不可用");
        _source.AddHook(WndProc);
    }

    public event EventHandler? Pressed;

    public bool IsRegistered => _registered;

    public int LastError { get; private set; }

    public bool TryRegister(string? gesture)
    {
        Unregister();
        LastError = 0;
        if (_disposed || !TryParse(gesture, out uint modifiers, out uint virtualKey))
        {
            return false;
        }

        _registered = NativeMethods.RegisterHotKey(
            _source.Handle, HotkeyId, modifiers | NativeMethods.ModNoRepeat, virtualKey);
        if (!_registered)
        {
            LastError = Marshal.GetLastWin32Error();
        }

        return _registered;
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    public static bool TryParse(string? gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        string[] parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.ModControl;
                    break;
                case "alt":
                    modifiers |= NativeMethods.ModAlt;
                    break;
                case "shift":
                    modifiers |= NativeMethods.ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= NativeMethods.ModWin;
                    break;
                default:
                    return false;
            }
        }

        if (modifiers == 0)
        {
            return false;
        }

        string key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            virtualKey = key[0];
            return true;
        }

        if (key.Length is 2 or 3 &&
            key[0] == 'F' &&
            int.TryParse(key.AsSpan(1), out int function) &&
            function is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + function - 1);
            return true;
        }

        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotkey || wParam.ToInt32() != HotkeyId)
        {
            return IntPtr.Zero;
        }

        Pressed?.Invoke(this, EventArgs.Empty);
        handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        _source.RemoveHook(WndProc);
    }
}
