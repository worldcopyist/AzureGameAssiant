using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AzureGameAssistant.UI.Controls;
using AzureGameAssistant.UI.ViewModels;

namespace AzureGameAssistant.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TimelineChart[] _charts;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _charts = [CpuChart, GpuChart, MemoryChart, VramChart, DiskChart];

        WindowSelector.SelectionChanged += OnWindowChanged;
        WindowSelector.SelectedIndex = 0;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public bool AllowClose { get; set; }

    public event EventHandler? SettingsRequested;

    public void RefreshChart()
    {
        UpdateChartColumns();

        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }

        foreach (TimelineChart chart in _charts)
        {
            chart.SetSamples(_viewModel.TimelineBuffer, _viewModel.TimelineCount);
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

    private void OnSettingsClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (AllowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
