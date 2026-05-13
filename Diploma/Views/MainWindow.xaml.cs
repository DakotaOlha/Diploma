using System.ComponentModel;
using System.Windows;
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
        SessionsViewModel sessionsViewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel  = viewModel;

        SessionsTab.Content = sessionsView;
        PlayerTab.Content   = playerView;

        sessionsViewModel.NavigateToPlayer += () =>
            PlayerTab.IsSelected = true;
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
        Hide();
    }
}