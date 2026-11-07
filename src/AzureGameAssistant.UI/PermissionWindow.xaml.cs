using System.Windows;
using AzureGameAssistant.UI.ViewModels;

namespace AzureGameAssistant.UI;

public partial class PermissionWindow : Window
{
    private readonly PermissionViewModel _viewModel;

    public PermissionWindow(PermissionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.ElevationRequested += OnElevationRequested;
    }

    public event EventHandler? ElevationRequested;

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.ElevationRequested -= OnElevationRequested;
        base.OnClosed(e);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnElevationRequested(object? sender, EventArgs e)
    {
        Close();
        ElevationRequested?.Invoke(this, EventArgs.Empty);
    }
}
