using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Diploma.ViewModels;

namespace Diploma.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isShutdownConfirmed;

    public MainWindow(
        MainViewModel     viewModel,
        SessionsView      sessionsView,
        PlayerView        playerView,
        SessionsViewModel sessionsViewModel,
        SettingsView      settingsView,
        SettingsViewModel settingsViewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel  = viewModel;

        SessionsTab.Content = sessionsView;
        PlayerTab.Content   = playerView;

        settingsView.DataContext = settingsViewModel;
        SettingsTab.Content      = settingsView;

        settingsView.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                settingsViewModel.Reload();
        };

        sessionsViewModel.NavigateToPlayer += () =>
            PlayerTab.IsSelected = true;

        StateChanged += MainWindow_StateChanged;
    }
    
    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
        else if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized 
            ? WindowState.Normal 
            : WindowState.Maximized;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (BtnMaximize != null)
        {
            BtnMaximize.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isShutdownConfirmed)
        {
            base.OnClosing(e);
            return;
        }
        
        if (!_viewModel.IsRecording)
        {
            e.Cancel = true;
            Hide();
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);

        Dispatcher.BeginInvoke(ConfirmStopAndExitAsync);
    }

    private async Task ConfirmStopAndExitAsync()
    {
        var result = MessageBox.Show(
            this,
            "Запис ще триває. Зупинити запис і закрити вікно?",
            "AlgoReplay",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            return;

        IsEnabled = false;

        try
        {
            if (_viewModel.IsRecording)
                await _viewModel.StopRecordingCommand.ExecuteAsync(null);
        }
        finally
        {
            IsEnabled = true;
        }

        _isShutdownConfirmed = true;
        Close();
    }
}