using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Diploma.Core.Interfaces;
using Diploma.Core.Services;
using Diploma.ViewModels;

namespace Diploma.Views;

public partial class OverlayWindow : Window
{
    private readonly IScreenCaptureService _captureService;
    private readonly IAudioCaptureService  _audioService;
    private readonly DiskSpaceService      _diskSpaceService;

    private System.Timers.Timer? _diskTimer;
    private volatile bool        _isClosing;
    private bool                 _isExpanded = true;

    private readonly DispatcherTimer _collapseTimer;

    private static readonly SolidColorBrush RedBrush    = Frozen(0xFF, 0x44, 0x44);
    private static readonly SolidColorBrush OrangeBrush = Frozen(0xFF, 0xA0, 0x00);
    private static readonly SolidColorBrush GrayBrush   = Frozen(0x9F, 0xA6, 0xA6);

    public OverlayWindow(
        MainViewModel         viewModel,
        IScreenCaptureService captureService,
        IAudioCaptureService  audioService,
        DiskSpaceService      diskSpaceService)
    {
        InitializeComponent();
        DataContext = viewModel;

        _captureService   = captureService;
        _audioService     = audioService;
        _diskSpaceService = diskSpaceService;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _collapseTimer.Tick += (_, _) => BeginCollapse();

        _captureService.RecordingStarted += OnRecordingStarted;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CenterAtTop();

        _diskTimer = new System.Timers.Timer(10_000) { AutoReset = true };
        _diskTimer.Elapsed += (_, _) =>
        {
            if (_isClosing || Dispatcher.HasShutdownStarted) return;
            try { Dispatcher.Invoke(UpdateDiskInfo); }
            catch (Exception) { }
        };
        _diskTimer.Start();

        UpdateDiskInfo();
    }

    private void OnClosed(object sender, EventArgs e)
    {
        _isClosing = true;
        _collapseTimer.Stop();
        _diskTimer?.Stop();
        _diskTimer?.Dispose();
        _diskTimer = null;
        _captureService.RecordingStarted -= OnRecordingStarted;
    }

    private void OnRecordingStarted(object? sender, EventArgs e)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (!_isExpanded) BeginExpand();
                else ResetCollapseTimer();
            });
        }
        catch (Exception) { }
    }

    // ── Expand / Collapse ────────────────────────────────────────────────────

    public new void Show()
    {
        base.Show();
        BeginExpand();
    }

    private void BeginExpand()
    {
        _isExpanded = true;
        _collapseTimer.Stop();

        CollapsedStrip.Visibility = Visibility.Collapsed;
        ExpandedBar.Visibility    = Visibility.Visible;

        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ExpandedBar.BeginAnimation(OpacityProperty, anim);

        ResetCollapseTimer();
    }

    private void BeginCollapse()
    {
        _collapseTimer.Stop();
        _isExpanded = false;

        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) =>
        {
            ExpandedBar.Visibility = Visibility.Collapsed;
            ExpandedBar.BeginAnimation(OpacityProperty, null);
            ExpandedBar.Opacity = 1;

            CollapsedStrip.Visibility = Visibility.Visible;
        };
        ExpandedBar.BeginAnimation(OpacityProperty, anim);
    }

    private void ResetCollapseTimer()
    {
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    // ── Mouse handlers ───────────────────────────────────────────────────────

    private void Bar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isExpanded) ResetCollapseTimer();
    }

    private void Strip_MouseEnter(object sender, MouseEventArgs e) => BeginExpand();

    private void Strip_Click(object sender, MouseButtonEventArgs e) => BeginExpand();

    private void Bar_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            _collapseTimer.Stop();
            DragMove();
            ResetCollapseTimer();
        }
    }

    // ── Button handlers ──────────────────────────────────────────────────────

    private void RecordBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.IsRecording) _ = vm.StopRecordingCommand.ExecuteAsync(null);
        else                _ = vm.StartRecordingCommand.ExecuteAsync(null);
    }

    private void MicBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.IsMicEnabled = !vm.IsMicEnabled;
    }

    private void SessionsBtn_Click(object sender, RoutedEventArgs e) =>
        NavigateMainWindow(1);

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) =>
        NavigateMainWindow(3);

    private static void NavigateMainWindow(int tabIndex)
    {
        if (App.Current.MainWindow is MainWindow mw)
        {
            mw.Show();
            if (mw.WindowState == WindowState.Minimized)
                mw.WindowState = WindowState.Normal;
            mw.Activate();
            mw.NavigateTo(tabIndex);
        }
    }

    // ── Public API (called from MainViewModel) ───────────────────────────────

    public void UpdateDropStats(long totalDropped, int currentFps)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (totalDropped == 0)
                {
                    DropDot.Visibility = Visibility.Collapsed;
                    return;
                }

                DropDot.Fill       = currentFps < 20 ? OrangeBrush : RedBrush;
                DropDot.Visibility = Visibility.Visible;
            });
        }
        catch (Exception) { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void CenterAtTop()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen
                     ?? System.Windows.Forms.Screen.AllScreens[0];
        Left = (screen.Bounds.Width - Width) / 2;
        Top  = 0;
    }

    private void UpdateDiskInfo()
    {
        var checkPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        try
        {
            var result = _diskSpaceService.Check(checkPath);
            var free   = _diskSpaceService.FormatFreeSpace(result.FreeBytes);
            var hours  = result.EstimatedHours >= 100 ? "∞" : $"{result.EstimatedHours:F0}h";
            DiskLabel.Text = $"{free}  ·  {hours}";

            DiskLabel.Foreground = result.EstimatedHours switch
            {
                < 0.5 => RedBrush,
                < 1.0 => OrangeBrush,
                _     => GrayBrush
            };
        }
        catch
        {
            DiskLabel.Text = "—";
        }
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var b2 = new SolidColorBrush(Color.FromRgb(r, g, b));
        b2.Freeze();
        return b2;
    }
}
