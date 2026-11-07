using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AzureGameAssistant.UI.Controls;
using AzureGameAssistant.UI.Services;
using AzureGameAssistant.UI.ViewModels;

namespace AzureGameAssistant.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TimelineChart[] _charts;
    private readonly Dictionary<ChartMetric, DetachedChart> _detached = new();

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _charts = [CombinedChart, FrameChart, CpuChart, GpuChart, MemoryChart, VramChart, DiskChart];

        WindowSelector.SelectionChanged += OnWindowChanged;
        WindowSelector.SelectedIndex = 0;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public bool AllowClose { get; set; }

    public event EventHandler? SettingsRequested;

    public event EventHandler? AiAnalysisRequested;

    public bool HasVisibleCharts
    {
        get
        {
            foreach (DetachedChart entry in _detached.Values)
            {
                if (entry.Window.IsVisible)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void RefreshChart()
    {
        if (DesignToggle.IsChecked == true && !_viewModel.ShowSeparateCharts)
        {
            DesignToggle.IsChecked = false;
        }

        bool mainVisible = IsVisible && WindowState != WindowState.Minimized;
        if (!mainVisible && !HasVisibleCharts)
        {
            return;
        }

        if (mainVisible)
        {
            UpdateChartColumns();
        }

        CombinedChart.Series = _viewModel.CombinedSeries;

        foreach (TimelineChart chart in _charts)
        {
            chart.SetSamples(_viewModel.TimelineBuffer, _viewModel.TimelineCount);
        }

        int frameCount = _viewModel.CopyFrameTimeline();
        FrameChart.SetFrames(_viewModel.FrameTimelineBuffer, frameCount);
        CombinedChart.SetFrames(_viewModel.FrameTimelineBuffer, frameCount);
    }

    public void RefreshLocalization()
    {
        LocalizationService loc = LocalizationService.Instance;
        foreach (DetachedChart entry in _detached.Values)
        {
            entry.Hint.Text = loc["ChartDetachedHint"];
            entry.DockButton.Content = loc["ChartRestore"];
            entry.Window.Title = loc[ChartLabelKey(entry.Metric)] + " · " + loc["AppTitle"];
        }

        foreach (TimelineChart chart in _charts)
        {
            chart.InvalidateVisual();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowSeconds(60);
        RefreshChart();
    }

    private void UpdateChartColumns()
    {
        int visible = 0;
        foreach (TimelineChart chart in _charts)
        {
            if (chart.IsVisible)
            {
                visible++;
            }
        }

        ChartGrid.Columns = visible == 1 ? 1 : 2;
    }

    private void ApplyWindowSeconds(double seconds)
    {
        foreach (TimelineChart chart in _charts)
        {
            chart.WindowSeconds = seconds;
        }
    }

    private void OnWindowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowSelector.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            double.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out double seconds))
        {
            ApplyWindowSeconds(seconds);
        }
    }

    private void OnDetachChart(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } button ||
            !Enum.TryParse(tag, out ChartMetric metric) ||
            _detached.ContainsKey(metric))
        {
            return;
        }

        TimelineChart? chart = ChartFor(metric);
        if (chart?.Parent is not Grid parent)
        {
            return;
        }

        parent.Children.Remove(chart);

        var hint = new TextBlock
        {
            Text = LocalizationService.Instance["ChartDetachedHint"],
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 260,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var dock = new Button
        {
            Content = LocalizationService.Instance["ChartRestore"],
            Style = (Style)FindResource("FlatButton"),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        dock.Click += (_, _) =>
        {
            if (_detached.TryGetValue(metric, out DetachedChart? entry))
            {
                entry.Window.Close();
            }
        };

        var placeholder = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        placeholder.Children.Add(hint);
        placeholder.Children.Add(dock);
        Grid.SetRow(placeholder, 1);
        parent.Children.Add(placeholder);

        string title = LocalizationService.Instance[ChartLabelKey(metric)] + " · " +
                       LocalizationService.Instance["AppTitle"];
        var window = new ChartWindow(chart, title);
        _detached[metric] = new DetachedChart(metric, window, parent, chart, placeholder, hint, dock, button);
        window.Closed += (_, _) => RestoreChart(metric);
        window.Show();

        button.IsEnabled = false;
        RefreshChart();
        UpdateChartColumns();
    }

    private void OnDesignModeChanged(object sender, RoutedEventArgs e)
    {
        if (DesignToggle.IsChecked == true)
        {
            return;
        }

        var metrics = new List<ChartMetric>(_detached.Keys);
        foreach (ChartMetric metric in metrics)
        {
            if (_detached.TryGetValue(metric, out DetachedChart? entry))
            {
                entry.Window.Close();
            }
        }
    }

    private void RestoreChart(ChartMetric metric)
    {
        if (!_detached.Remove(metric, out DetachedChart? entry))
        {
            return;
        }

        entry.Window.ReleaseChart();
        entry.Parent.Children.Remove(entry.Placeholder);
        entry.Parent.Children.Add(entry.Chart);
        Grid.SetRow(entry.Chart, 1);
        entry.DetachButton.IsEnabled = true;
        UpdateChartColumns();
    }

    private TimelineChart? ChartFor(ChartMetric metric) => metric switch
    {
        ChartMetric.Combined => CombinedChart,
        ChartMetric.Cpu => CpuChart,
        ChartMetric.Gpu => GpuChart,
        ChartMetric.Memory => MemoryChart,
        ChartMetric.Vram => VramChart,
        ChartMetric.Disk => DiskChart,
        ChartMetric.Frame => FrameChart,
        _ => null,
    };

    private static string ChartLabelKey(ChartMetric metric) => metric switch
    {
        ChartMetric.Cpu => "Cpu",
        ChartMetric.Gpu => "Gpu",
        ChartMetric.Memory => "Memory",
        ChartMetric.Vram => "Vram",
        ChartMetric.Frame => "FrameChartLabel",
        _ => "Disk",
    };

    private void OnSettingsClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnAiAnalyzeClick(object sender, RoutedEventArgs e) =>
        AiAnalysisRequested?.Invoke(this, EventArgs.Empty);

    private void OnFrameTargetDropDownOpened(object? sender, EventArgs e) =>
        _viewModel.RefreshFrameTargetsCommand.Execute(null);

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (AllowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private sealed record DetachedChart(
        ChartMetric Metric,
        ChartWindow Window,
        Grid Parent,
        TimelineChart Chart,
        StackPanel Placeholder,
        TextBlock Hint,
        Button DockButton,
        Button DetachButton);
}
