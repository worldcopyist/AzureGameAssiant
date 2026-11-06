using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AzureGameAssistant.UI.Interop;

namespace AzureGameAssistant.UI.Services;

/// <summary>
/// Tray icon built on Shell_NotifyIcon. The main window handle receives the
/// callback messages, so the icon keeps working while the window is hidden.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint IconId = 1;

    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private string _tip = string.Empty;
    private bool _added;
    private bool _disposed;

    public TrayIconService(IntPtr windowHandle)
    {
        _source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException("主窗口句柄不可用");
        _source.AddHook(WndProc);
        _icon = NativeMethods.LoadIconW(IntPtr.Zero, (IntPtr)NativeMethods.IdiApplication);
        Add("Azure Game Assistant");
    }

    public event EventHandler? OpenRequested;

    public event EventHandler<Point>? MenuRequested;

    public void UpdateTip(string tip)
    {
        if (_disposed || string.Equals(tip, _tip, StringComparison.Ordinal))
        {
            return;
        }

        _tip = tip;
        NativeMethods.NotifyIconData data = CreateData(NativeMethods.NifTip);
        Modify(ref data);
    }

    public void ShowBalloon(string title, string text)
    {
        if (_disposed || !_added)
        {
            return;
        }

        NativeMethods.NotifyIconData data = CreateData(NativeMethods.NifInfo);
        unsafe
        {
            NativeMethods.CopyString(data.InfoTitle, 64, title);
            NativeMethods.CopyString(data.Info, 256, text);
        }

        data.InfoFlags = NativeMethods.NiifInfo;
        Modify(ref data);
    }

    private void Add(string tip)
    {
        _tip = tip;
        NativeMethods.NotifyIconData data = CreateData(NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip);
        unsafe
        {
            NativeMethods.CopyString(data.Tip, 128, tip);
        }

        _added = NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, ref data);
    }

    private void Modify(ref NativeMethods.NotifyIconData data)
    {
        if (!_added)
        {
            return;
        }

        NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref data);
    }

    private unsafe NativeMethods.NotifyIconData CreateData(uint flags)
    {
        var data = new NativeMethods.NotifyIconData
        {
            Size = sizeof(NativeMethods.NotifyIconData),
            Window = _source.Handle,
            Id = IconId,
            Flags = flags,
            CallbackMessage = NativeMethods.TrayCallbackMessage,
            Icon = _icon,
        };

        return data;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.TrayCallbackMessage)
        {
            return IntPtr.Zero;
        }

        switch ((int)lParam)
        {
            case NativeMethods.WmRButtonUp:
                ShowMenu(hwnd);
                handled = true;
                break;

            case NativeMethods.WmLButtonDblClk:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu(IntPtr hwnd)
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.Point32 point))
        {
            return;
        }

        NativeMethods.SetForegroundWindow(hwnd);
        MenuRequested?.Invoke(this, new Point(point.X, point.Y));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(WndProc);

        if (_added)
        {
            NativeMethods.NotifyIconData data = CreateData(0);
            NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data);
            _added = false;
        }
    }
}
