using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AzureGameAssistant.UI.Interop;

namespace AzureGameAssistant.UI.Services;

/// <summary>
/// Applies the active palette to the native title bar. Without this the
/// non-client area keeps the system light chrome and clashes with dark themes.
/// </summary>
internal static class WindowChromeTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void Apply(Window window)
    {
        if (window.WindowStyle == WindowStyle.None)
        {
            return;
        }

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int dark = ThemeService.IsDark ? 1 : 0;
        if (NativeMethods.DwmSetWindowAttribute(
                handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            NativeMethods.DwmSetWindowAttribute(
                handle, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
        }

        // Caption colors need Windows 11; Windows 10 only has the dark flag.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        if (window.Background is SolidColorBrush background)
        {
            int caption = ToColorRef(background.Color);
            NativeMethods.DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
            NativeMethods.DwmSetWindowAttribute(handle, DwmwaBorderColor, ref caption, sizeof(int));
        }

        if (window.Foreground is SolidColorBrush foreground)
        {
            int text = ToColorRef(foreground.Color);
            NativeMethods.DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
        }
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
}
