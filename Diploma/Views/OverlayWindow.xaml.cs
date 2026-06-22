using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using Diploma.Core.Services;
using Diploma.ViewModels;

namespace Diploma.Views;

public partial class OverlayWindow : Window
{
    private readonly IScreenCaptureService _captureService;
    private readonly IAudioCaptureService  _audioService;
    private readonly DiskSpaceService      _diskSpaceService;
    private readonly IInputMonitorService  _inputMonitor;
    private readonly ISettingsService      _settingsService;
    private readonly IGlobalHotkeyService  _hotkeyService;

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
        IInputMonitorService  inputMonitor,
        ISettingsService      settingsService,
        IGlobalHotkeyService  hotkeyService)
    {
        InitializeComponent();
        DataContext = viewModel;

        _captureService   = captureService;
        _audioService     = audioService;
        _diskSpaceService = diskSpaceService;
        _inputMonitor     = inputMonitor;
        _settingsService  = settingsService;
        _hotkeyService    = hotkeyService;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _collapseTimer.Tick += (_, _) => BeginCollapse();

        _captureService.RecordingStarted   += OnRecordingStarted;
        _inputMonitor.ViolationDetected    += OnViolationDetected;
        _hotkeyService.WhiteboardRequested += OnWhiteboardRequested;

        if (viewModel is System.ComponentModel.INotifyPropertyChanged npc)
            npc.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
        Closed += OnClosed!;
    }

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
        _captureService.RecordingStarted   -= OnRecordingStarted;
        _inputMonitor.ViolationDetected    -= OnViolationDetected;
        _hotkeyService.WhiteboardRequested -= OnWhiteboardRequested;
        if (DataContext is System.ComponentModel.INotifyPropertyChanged npc)
            npc.PropertyChanged -= OnViewModelPropertyChanged;
    }

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

    private void OnViolationDetected(object? sender, string message)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        try { Dispatcher.Invoke(() => ShowViolationBanner(message)); }
        catch (Exception) { }
    }

    private void ShowViolationBanner(string message)
    {
        var vm = DataContext as MainViewModel;
        if (vm?.SelectedMode?.Mode == RecordingMode.Olympic
            && !_settingsService.Current.OlympicShowViolationToast)
            return;

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

        if (_violationTimer != null)
            ViolationPanel.Visibility = Visibility.Visible;

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
            ExpandedBar.Visibility    = Visibility.Collapsed;
            ViolationPanel.Visibility = Visibility.Collapsed;
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

    private void Bar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isExpanded) ResetCollapseTimer();
    }

    private void Strip_MouseEnter(object sender, MouseEventArgs e) => BeginExpand();

    private void Strip_Click(object sender, MouseButtonEventArgs e) => BeginExpand();

    private void RecordBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        if (vm.IsRecording)
        {
            _ = vm.StopRecordingCommand.ExecuteAsync(null);
        }
        else
        {
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

    private WhiteboardWindow? _whiteboard;

    private void OnWhiteboardRequested(object? sender, EventArgs e)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        try { Dispatcher.Invoke(OpenOrFocusWhiteboard); }
        catch (Exception) { }
    }

    private void WhiteboardBtn_Click(object sender, RoutedEventArgs e) =>
        OpenOrFocusWhiteboard();

    private void OpenOrFocusWhiteboard()
    {
        if (_whiteboard is { IsLoaded: true })
        {
            if (_whiteboard.WindowState == WindowState.Minimized)
                _whiteboard.WindowState = WindowState.Normal;
            _whiteboard.Activate();
            return;
        }
        _whiteboard = new WhiteboardWindow();
        _whiteboard.Closed += (_, _) => _whiteboard = null;
        _whiteboard.Show();
    }

    private void SessionsBtn_Click(object sender, RoutedEventArgs e) =>
        NavigateMainWindow(0);

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

    private void CenterAtTop()
    {
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
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