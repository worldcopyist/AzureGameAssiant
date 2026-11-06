using System.Diagnostics;
using System.Windows;
using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Settings;
using AzureGameAssistant.UI.ViewModels;
using System.IO;

namespace AzureGameAssistant.UI;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly AppSettings _settings;

    public SettingsWindow(SettingsViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;
    }

    public event EventHandler<AppSettings>? SaveRequested;

    private void OnApply(object sender, RoutedEventArgs e)
    {
        _viewModel.ApplyTo(_settings);
        SaveRequested?.Invoke(this, _settings);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = AppPaths.LogsDirectory,
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
