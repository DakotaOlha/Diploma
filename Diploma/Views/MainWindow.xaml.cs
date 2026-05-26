using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Diploma.ViewModels;

namespace Diploma.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isShutdownConfirmed;

    // ── WM_GETMINMAXINFO hook — ensures maximized window respects taskbar ──

    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src)
            src.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
                                  ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            ApplyWorkAreaConstraints(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ApplyWorkAreaConstraints(IntPtr hwnd, IntPtr lParam)
    {
        var mmi    = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
        var wa     = screen.WorkingArea;
        var full   = screen.Bounds;

        mmi.ptMaxPosition.X = wa.Left - full.Left;
        mmi.ptMaxPosition.Y = wa.Top  - full.Top;
        mmi.ptMaxSize.X     = wa.Width;
        mmi.ptMaxSize.Y     = wa.Height;

        Marshal.StructureToPtr(mmi, lParam, true);
    }

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
            BtnMaximize.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    public void NavigateTo(int tabIndex)
    {
        MainTabControl.SelectedIndex = tabIndex;
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