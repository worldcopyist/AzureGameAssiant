using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AzureGameAssistant.Core.Overlay;
using AzureGameAssistant.Core.Settings;
using AzureGameAssistant.UI.Interop;
using AzureGameAssistant.UI.Services;
using AzureGameAssistant.UI.ViewModels;

namespace AzureGameAssistant.UI;

public partial class OverlayWindow : Window
{
    private readonly List<OverlayEntry> _entries = new();
    private OverlaySettings _settings = new();

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
    }

    public void ApplySettings(OverlaySettings settings)
    {
        _settings = settings;
        ApplyAppearance();
        RebuildRows();
    }

    public void RefreshLocalization() => RebuildRows();

    public void ShowOverlay()
    {
        Show();
        UpdateLayout();
        Reposition();
        Update(default);
    }

    public void Update(in OverlayData data)
    {
        foreach (OverlayEntry entry in _entries)
        {
            string text = entry.Field switch
            {
                OverlayField.Fps => data.HasFrames ? data.Fps.ToString("F0") : "—",
                OverlayField.AverageFps => data.HasFrames ? data.AverageFps.ToString("F0") : "—",
                OverlayField.LowFps => data.HasFrames ? data.LowFps.ToString("F0") : "—",
                OverlayField.Cpu => data.HasSample ? data.CpuPct.ToString("F0") + "%" : "—",
                OverlayField.Gpu => data.HasSample ? data.GpuPct.ToString("F0") + "%" : "—",
                OverlayField.Temperature => data.HasTemperature ? data.TemperatureC.ToString("F0") + " °C" : "—",
                OverlayField.CpuTemperature => data.HasCpuTemperature ? data.CpuTemperatureC.ToString("F0") + " °C" : "—",
                OverlayField.Memory => data.HasSample ? data.MemoryPct.ToString("F0") + "%" : "—",
                OverlayField.Vram => FormatVram(data),
                _ => "—",
            };

            if (!string.Equals(entry.Value.Text, text, StringComparison.Ordinal))
            {
                entry.Value.Text = text;
            }
        }
    }

    private static string FormatVram(in OverlayData data)
    {
        if (!data.HasSample || data.VramMb <= 0)
        {
            return "—";
        }

        string used = (data.VramMb / 1024.0).ToString("F1") + " GB";
        return data.VramTotalMb > 0
            ? used + " / " + (data.VramTotalMb / 1024.0).ToString("F1") + " GB"
            : used;
    }

    private void ApplyAppearance()
    {
        (Color value, Color label, Color shadow) = ResolveAppearance(_settings);
        Opacity = Math.Clamp(_settings.Opacity, 30, 100) / 100.0;
        Resources["OverlayValueBrush"] = FrozenBrush(value);
        Resources["OverlayLabelBrush"] = FrozenBrush(label);
        byte backdropAlpha = (byte)Math.Round(Math.Clamp(_settings.BackdropOpacity, 0, 80) * 255.0 / 100.0);
        if (backdropAlpha == 0)
        {
            Resources["OverlayBackdropBrush"] = Brushes.Transparent;
            Backdrop.Padding = new Thickness(0);
        }
        else
        {
            Resources["OverlayBackdropBrush"] = FrozenBrush(Color.FromArgb(backdropAlpha, shadow.R, shadow.G, shadow.B));
            Backdrop.Padding = new Thickness(6);
        }

        Resources["OverlayTextShadow"] = new DropShadowEffect
        {
            Color = shadow,
            BlurRadius = 3,
            ShadowDepth = 0,
            Opacity = 0.9,
        };
    }

    private static (Color Value, Color Label, Color Shadow) ResolveAppearance(OverlaySettings settings)
    {
        switch (settings.Theme)
        {
            case "Light":
                return (Color.FromRgb(0x10, 0x14, 0x18), Color.FromRgb(0x46, 0x52, 0x5F), Colors.White);

            case "Azure":
                return (Color.FromRgb(0x5F, 0xC0, 0xFF), Color.FromRgb(0xB9, 0xE2, 0xFF), Colors.Black);

            case "Custom":
                (byte alpha, byte r, byte g, byte b) = TryParseColor(settings.Color);
                Color value = Color.FromArgb(alpha, r, g, b);
                Color label = Color.FromArgb((byte)Math.Round(alpha * 0xC8 / 255.0), r, g, b);
                byte shadowLevel = Luminance(value) > 0.55 ? (byte)0 : (byte)255;
                Color shadow = Color.FromArgb(alpha, shadowLevel, shadowLevel, shadowLevel);
                return (value, label, shadow);

            default:
                return (Colors.White, Color.FromRgb(0xD6, 0xDE, 0xE8), Colors.Black);
        }
    }

    private static (byte A, byte R, byte G, byte B) TryParseColor(string? text) =>
        OverlayColor.TryParse(text, out (byte A, byte R, byte G, byte B) color)
            ? color
            : ((byte)255, (byte)255, (byte)255, (byte)255);

    private static double Luminance(in Color color) =>
        ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;

    private static SolidColorBrush FrozenBrush(in Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void RebuildRows()
    {
        Rows.Children.Clear();
        _entries.Clear();

        LocalizationService loc = LocalizationService.Instance;
        if (_settings.ShowFps)
        {
            AddRow(OverlayField.Fps, loc["OverlayFps"]);
        }

        if (_settings.ShowFrameTimes)
        {
            AddRow(OverlayField.AverageFps, loc["OverlayAvg"]);
            AddRow(OverlayField.LowFps, loc["OverlayLow"]);
        }

        if (_settings.ShowCpu)
        {
            AddRow(OverlayField.Cpu, loc["Cpu"]);
        }

        if (_settings.ShowGpu)
        {
            AddRow(OverlayField.Gpu, loc["Gpu"]);
        }

        if (_settings.ShowTemperature)
        {
            AddRow(OverlayField.Temperature, loc["OverlayTemp"]);
        }

        if (_settings.ShowCpuTemperature)
        {
            AddRow(OverlayField.CpuTemperature, loc["OverlayCpuTemp"]);
        }

        if (_settings.ShowMemory)
        {
            AddRow(OverlayField.Memory, loc["OverlayMem"]);
        }

        if (_settings.ShowVram)
        {
            AddRow(OverlayField.Vram, loc["Vram"]);
        }

        Reposition();
    }

    private void AddRow(OverlayField field, string label)
    {
        var item = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 1, 16, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var caption = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("OverlayLabel"),
            Margin = new Thickness(0, 0, 5, 0),
        };

        var value = new TextBlock
        {
            Text = "—",
            Style = (Style)FindResource("OverlayValue"),
        };

        item.Children.Add(caption);
        item.Children.Add(value);
        Rows.Children.Add(item);
        _entries.Add(new OverlayEntry(field, value));
    }

    public void RefreshPlacement() => Reposition();

    private void Reposition()
    {
        if (!IsVisible)
        {
            return;
        }

        Rect work = TargetWorkArea();
        double maxWidth = Math.Max(240, work.Width - (_settings.OffsetX * 2.0));
        if (Math.Abs(Rows.MaxWidth - maxWidth) > 0.5)
        {
            Rows.MaxWidth = maxWidth;
            UpdateLayout();
        }

        double width = Math.Max(ActualWidth, 1);
        double height = Math.Max(ActualHeight, 1);
        (Left, Top) = OverlayPlacement.Place(
            _settings.Corner,
            work.Left,
            work.Top,
            work.Width,
            work.Height,
            width,
            height,
            _settings.OffsetX,
            _settings.OffsetY);
    }

    /// <summary>
    /// Uses the main window's monitor so the overlay follows the app across
    /// displays; DIP conversion matches the system-DPI coordinate space WPF
    /// positions windows in. Any failure keeps the primary work area.
    /// </summary>
    private Rect TargetWorkArea()
    {
        Window? main = Application.Current?.MainWindow;
        IntPtr handle = main is null ? IntPtr.Zero : new WindowInteropHelper(main).Handle;
        if (!MonitorWorkArea.TryGet(handle, out NativeMethods.Rect32 work))
        {
            return SystemParameters.WorkArea;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
        return new Rect(
            work.Left / scaleX,
            work.Top / scaleY,
            (work.Right - work.Left) / scaleX,
            (work.Bottom - work.Top) / scaleY);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        long style = NativeMethods.GetWindowLongPtrW(handle, NativeMethods.GwlExStyle).ToInt64();
        style |= NativeMethods.WsExTransparent | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLongPtrW(handle, NativeMethods.GwlExStyle, new IntPtr(style));
    }

    private enum OverlayField
    {
        Fps,
        AverageFps,
        LowFps,
        Cpu,
        Gpu,
        Temperature,
        CpuTemperature,
        Memory,
        Vram,
    }

    private sealed record OverlayEntry(OverlayField Field, TextBlock Value);
}
