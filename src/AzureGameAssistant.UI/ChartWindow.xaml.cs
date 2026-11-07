using System.Windows;
using AzureGameAssistant.UI.Controls;

namespace AzureGameAssistant.UI;

public partial class ChartWindow : Window
{
    public ChartWindow(TimelineChart chart, string title)
    {
        InitializeComponent();
        Title = title;
        Host.Content = chart;
    }

    public void ReleaseChart() => Host.Content = null;
}
