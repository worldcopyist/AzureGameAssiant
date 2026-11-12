using System.Windows;
using AzureGameAssistant.Core.Overlay;
using AzureGameAssistant.Core.Settings;
using AzureGameAssistant.UI.Controls;

namespace AzureGameAssistant.UI;

public partial class ChartWindow : Window
{
    public ChartWindow(TimelineChart chart, string title, DetachedChartSettings placement)
    {
        InitializeComponent();
        Title = title;
        Host.Content = chart;
        Topmost = placement.Topmost;
        TopmostMenuItem.IsChecked = placement.Topmost;

        if (ChartWindowPlacement.TryRestore(
                placement.Left,
                placement.Top,
                placement.Width,
                placement.Height,
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight,
                out (double left, double top, double width, double height) bounds))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.left;
            Top = bounds.top;
            Width = bounds.width;
            Height = bounds.height;
        }
    }

    public event EventHandler? PlacementChanged;

    public void ReleaseChart() => Host.Content = null;

    private void OnTopmostToggled(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostMenuItem.IsChecked;
        PlacementChanged?.Invoke(this, EventArgs.Empty);
    }
}
