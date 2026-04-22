using System.Windows;
using Diploma.ViewModels;

namespace Diploma.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel, SessionsView sessionsView, PlayerView playerView, SessionsViewModel sessionsViewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    
        SessionsTab.Content = sessionsView;
        PlayerTab.Content = playerView;
    
        sessionsViewModel.NavigateToPlayer += () =>
        {
            PlayerTab.IsSelected = true;
        };
    }
}