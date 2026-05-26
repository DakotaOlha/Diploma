using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Diploma.ViewModels;

namespace Diploma.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsView settingsView, SettingsViewModel settingsViewModel)
    {
        InitializeComponent();
        _viewModel = settingsViewModel;

        settingsView.DataContext = settingsViewModel;
        ContentArea.Content     = settingsView;
    }

    public new void Show()
    {
        _viewModel.Reload();
        if (IsVisible) { Activate(); return; }
        base.Show();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Hide(); }
        base.OnKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
