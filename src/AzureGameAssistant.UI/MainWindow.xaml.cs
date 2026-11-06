using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AzureGameAssistant.UI.ViewModels;

namespace AzureGameAssistant.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _timer;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _timer.Tick += OnTick;

        Loaded += OnLoaded;
        IsVisibleChanged += OnVisibleChanged;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        Closed += (_, _) => _timer.Stop();
    }

    public bool AllowClose { get; set; }

    public event EventHandler? SettingsRequested;

    public void RefreshChart()
    {
        Chart.SetSamples(_viewModel.TimelineBuffer, _viewModel.TimelineCount);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshChart();
        _timer.Start();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        await _viewModel.RefreshAsync();
        RefreshChart();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _timer.Stop();
        }
        else if (IsVisible)
        {
            _timer.Start();
        }
    }

    private void OnWindowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Chart is null)
        {
            return;
        }

        if (WindowSelector.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            double.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out double seconds))
        {
            Chart.WindowSeconds = seconds;
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
