using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly IInputMonitorService  _inputMonitor;

    private System.Timers.Timer? _diskTimer;
    private DispatcherTimer?     _violationTimer;
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
        DiskSpaceService      diskSpaceService,
        IInputMonitorService  inputMonitor)
    {
        InitializeComponent();
        DataContext = viewModel;

        _captureService   = captureService;
        _audioService     = audioService;
        _diskSpaceService = diskSpaceService;
        _inputMonitor     = inputMonitor;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _collapseTimer.Tick += (_, _) => BeginCollapse();

        _captureService.RecordingStarted   += OnRecordingStarted;
        _inputMonitor.ViolationDetected    += OnViolationDetected;

        if (viewModel is System.ComponentModel.INotifyPropertyChanged npc)
            npc.PropertyChanged += OnViewModelPropertyChanged;

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
        _inputMonitor.ViolationDetected  -= OnViolationDetected;
        if (DataContext is System.ComponentModel.INotifyPropertyChanged npc)
            npc.PropertyChanged -= OnViewModelPropertyChanged;
    }

    // Re-expand if the recording start was cancelled (picker dismissed or error).
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.IsBusy) or nameof(MainViewModel.IsRecording)))
            return;
        if (DataContext is not MainViewModel vm) return;
        if (!vm.IsBusy && !vm.IsRecording && !_isExpanded)
        {
            try { Dispatcher.Invoke(BeginExpand); }
            catch (Exception) { }
        }
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

    // ── Violation banner ─────────────────────────────────────────────────────

    private void OnViolationDetected(object? sender, string message)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        try { Dispatcher.Invoke(() => ShowViolationBanner(message)); }
        catch (Exception) { }
    }

    private void ShowViolationBanner(string message)
    {
        ViolationText.Text        = message;
        ViolationPanel.Visibility = Visibility.Visible;

        if (!_isExpanded) BeginExpand();

        _violationTimer?.Stop();
        _violationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _violationTimer.Tick += (_, _) =>
        {
            _violationTimer?.Stop();
            _violationTimer = null;
            ViolationPanel.Visibility = Visibility.Collapsed;
        };
        _violationTimer.Start();
    }

    // ── Expand / Collapse ─────────────────────────────────────────────────────

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

    // ── Button handlers ──────────────────────────────────────────────────────

    private void RecordBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        if (vm.IsRecording)
        {
            _ = vm.StopRecordingCommand.ExecuteAsync(null);
        }
        else
        {
            // Collapse to 4-px strip — window stays alive (keeps app foreground
            // status) so the system GraphicsCapturePicker can appear.
            // CaptureTargetSelected will call Show() → BeginExpand() once the
            // window is chosen.
            BeginCollapse();
            _ = vm.StartRecordingCommand.ExecuteAsync(null);
        }
    }

    private void MicBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var menu = new ContextMenu { StaysOpen = false };

        var noMic = new MenuItem { Header = "Без мікрофону" };
        if (!vm.IsMicEnabled) noMic.IsChecked = true;
        noMic.Click += (_, _) => vm.IsMicEnabled = false;
        menu.Items.Add(noMic);

        if (vm.MicDevices.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var device in vm.MicDevices)
            {
                var item = new MenuItem { Header = device };
                if (vm.IsMicEnabled && vm.SelectedMicDevice == device)
                    item.IsChecked = true;
                var captured = device;
                item.Click += (_, _) =>
                {
                    vm.SelectedMicDevice = captured;
                    vm.IsMicEnabled      = true;
                };
                menu.Items.Add(item);
            }
        }

        menu.PlacementTarget = (UIElement)sender;
        menu.Placement       = PlacementMode.Bottom;
        menu.IsOpen          = true;
    }

    private void SessionsBtn_Click(object sender, RoutedEventArgs e) =>
        NavigateMainWindow(0); // Sessions is now the first (index 0) tab

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) =>
        ((App)App.Current).GetSettingsWindow().Show();

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
