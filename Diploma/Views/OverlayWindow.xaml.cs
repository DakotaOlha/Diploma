using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Diploma.Core.Interfaces;
using Diploma.Core.Services;
using Diploma.ViewModels;

namespace Diploma.Views;

public partial class OverlayWindow : Window
{
    private readonly IScreenCaptureService _captureService;
    private readonly IAudioCaptureService  _audioService;
    private readonly DiskSpaceService      _diskSpaceService;

    private string    _videoOutputPath = string.Empty;
    private DateTime? _videoStartTime;
    private DateTime? _audioStartTime;
    
    private System.Timers.Timer? _updateTimer;
    private System.Timers.Timer? _diskTimer;
    
    private volatile bool _isClosing;

    private static readonly SolidColorBrush GreenBrush  = Frozen(0x4C, 0xAF, 0x50);
    private static readonly SolidColorBrush GrayBrush   = Frozen(0x88, 0x88, 0x88);
    private static readonly SolidColorBrush RedBrush    = Frozen(0xFF, 0x55, 0x55);
    private static readonly SolidColorBrush OrangeBrush = Frozen(0xFF, 0xA0, 0x00);

    public OverlayWindow(
        MainViewModel          viewModel,
        IScreenCaptureService  captureService,
        IAudioCaptureService   audioService,
        DiskSpaceService       diskSpaceService)
    {
        InitializeComponent();
        DataContext = viewModel;

        _captureService   = captureService;
        _audioService     = audioService;
        _diskSpaceService = diskSpaceService;

        _captureService.RecordingStarted += OnRecordingStarted;

        Loaded += OnLoaded;
        Closed += OnClosed;

        UpdateDiskInfo();
    }
    
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
 
        _updateTimer = new System.Timers.Timer(200) { AutoReset = true };
        _updateTimer.Elapsed += OnUpdateTimerElapsed;
        _updateTimer.Start();
 
        _diskTimer = new System.Timers.Timer(10_000) { AutoReset = true };
        _diskTimer.Elapsed += OnDiskTimerElapsed;
        _diskTimer.Start();
    }
    
    private void OnClosed(object sender, EventArgs e)
    {
        _isClosing = true;
 
        _updateTimer?.Stop();
        _diskTimer?.Stop();
 
        _updateTimer?.Dispose();
        _diskTimer?.Dispose();
 
        _updateTimer = null;
        _diskTimer   = null;
 
        _captureService.RecordingStarted -= OnRecordingStarted;
    }
    
    private void OnUpdateTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
 
        try
        {
            Dispatcher.Invoke(UpdateTimers);
        }
        catch (TaskCanceledException) { /* Dispatcher shut down mid-flight */ }
        catch (InvalidOperationException) { /* Dispatcher already shut down */ }
    }
    
    private void OnDiskTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
 
        try
        {
            Dispatcher.Invoke(UpdateDiskInfo);
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }
    
    private void OnRecordingStarted(object? sender, EventArgs e)
    {
        _videoStartTime = DateTime.Now;
        _audioStartTime = DateTime.Now;
 
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
 
        try
        {
            Dispatcher.Invoke(() =>
            {
                VideoStatusDot.Foreground = GreenBrush;
                AudioStatusDot.Foreground = GreenBrush;
            });
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }

    public void SetOutputPath(string path) => _videoOutputPath = path;

    public void UpdateDropStats(long totalDropped, int currentFps)
    {
        if (totalDropped == 0)
        {
            DropStatsText.Text       = string.Empty;
            DropStatsText.Visibility = Visibility.Collapsed;
            return;
        }

        var fpsNote = currentFps < 30 ? $"  {currentFps} fps ↓" : string.Empty;
        DropStatsText.Text       = $"⚠️ {totalDropped} dropped{fpsNote}";
        DropStatsText.Foreground = currentFps < 30 ? OrangeBrush : RedBrush;
        DropStatsText.Visibility = Visibility.Visible;
    }

    private void UpdateDiskInfo()
    {
        var checkPath = string.IsNullOrEmpty(_videoOutputPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            : _videoOutputPath;

        try
        {
            var result = _diskSpaceService.Check(checkPath);

            DiskFreeText.Text     = $"💾 {_diskSpaceService.FormatFreeSpace(result.FreeBytes)}";
            DiskEstimateText.Text = result.EstimatedHours >= 100
                ? "~∞"
                : $"~{result.EstimatedHours:F1}h";

            var color = result.EstimatedHours switch
            {
                < 0.5 => RedBrush,
                < 1.0 => OrangeBrush,
                _     => GrayBrush
            };

            DiskFreeText.Foreground     = color;
            DiskEstimateText.Foreground = color;
        }
        catch
        {
            DiskFreeText.Text    = "💾 —";
            DiskEstimateText.Text = "";
        }
    }

    private void UpdateTimers()
    {
        var now = DateTime.Now;
        
        if (_videoStartTime.HasValue && _captureService.IsRecording)
        {
            var elapsed = now - _videoStartTime.Value;
            VideoTimerText.Text       = elapsed.ToString(@"hh\:mm\:ss\.f");
            VideoTimerText.Foreground = GreenBrush;
            VideoStatusDot.Foreground = GreenBrush;
        }
        else
        {
            VideoTimerText.Text       = "00:00:00.0";
            VideoTimerText.Foreground = GrayBrush;
            VideoStatusDot.Foreground = GrayBrush;
        }
 
        if (_audioStartTime.HasValue && _audioService.IsRecording)
        {
            var elapsed = now - _audioStartTime.Value;
            AudioTimerText.Text       = elapsed.ToString(@"hh\:mm\:ss\.f");
            AudioTimerText.Foreground = GreenBrush;
            AudioStatusDot.Foreground = GreenBrush;
        }
        else
        {
            AudioTimerText.Text       = "00:00:00.0";
            AudioTimerText.Foreground = GrayBrush;
            AudioStatusDot.Foreground = GrayBrush;
        }
 
        if (_videoStartTime.HasValue && _audioStartTime.HasValue
            && _captureService.IsRecording && _audioService.IsRecording)
        {
            var videoDelta = (now - _videoStartTime.Value).TotalSeconds;
            var audioDelta = (now - _audioStartTime.Value).TotalSeconds;
            var delta      = videoDelta - audioDelta;
 
            DeltaText.Text = $"Δ {delta:+0.0;-0.0;0.0}s " +
                             (delta > 0 ? "(audio lags)" :
                              delta < 0 ? "(video lags)" : "(in sync)");
 
            DeltaText.Foreground = Math.Abs(delta) > 1.0 ? RedBrush : GrayBrush;
        }
        else
        {
            DeltaText.Text       = "Δ —";
            DeltaText.Foreground = GrayBrush;
        }
 
        if (!_captureService.IsRecording && _videoStartTime.HasValue)
        {
            _videoStartTime = null;
            _audioStartTime = null;
        }
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) 
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void MarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            _ = vm.AddMarkerCommand.ExecuteAsync(null);
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}