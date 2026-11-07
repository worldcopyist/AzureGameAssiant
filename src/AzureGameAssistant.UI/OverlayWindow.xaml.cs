using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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

    private void Reposition()
    {
        if (!IsVisible)
        {
            return;
        }

        Rect work = SystemParameters.WorkArea;
        double maxWidth = Math.Max(240, work.Width - (_settings.OffsetX * 2.0));
        if (Math.Abs(Rows.MaxWidth - maxWidth) > 0.5)
        {
            Rows.MaxWidth = maxWidth;
            UpdateLayout();
        }

        double width = Math.Max(ActualWidth, 1);
        double height = Math.Max(ActualHeight, 1);
        bool right = _settings.Corner is "TopRight" or "BottomRight";
        bool bottom = _settings.Corner is "BottomLeft" or "BottomRight";

        Left = right ? work.Right - width - _settings.OffsetX : work.Left + _settings.OffsetX;
        Top = bottom ? work.Bottom - height - _settings.OffsetY : work.Top + _settings.OffsetY;
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
        Memory,
        Vram,
    }

    private sealed record OverlayEntry(OverlayField Field, TextBlock Value);
}
