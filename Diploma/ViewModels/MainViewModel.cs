using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using Diploma.Core.Services;
using Diploma.Views;

namespace Diploma.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public bool CanSelectMode => !IsRecording;
    public bool CanAddMarker  => IsRecording;

    [ObservableProperty] private bool     _isRecording;
    [ObservableProperty] private string   _statusText       = "Ready";
    [ObservableProperty] private TimeSpan _recordingDuration;

    [ObservableProperty] private string  _dropWarning      = string.Empty;
    [ObservableProperty] private bool    _hasDropWarning;
    [ObservableProperty] private int     _currentFps       = 30;

    private System.Timers.Timer? _durationTimer;

    private readonly IScreenCaptureService  _captureService;
    private readonly ILogService            _logService;
    private readonly IInputMonitorService   _inputMonitor;
    private readonly IAudioCaptureService   _audioCaptureService;
    private readonly ModeProfileService     _profileService;
    private readonly DiskSpaceService       _diskSpaceService;
    private readonly MediaMergeService      _mediaMergeService;
    private readonly IGlobalHotkeyService   _hotkeyService;

    private string _currentAudioPath = string.Empty;
    private string _currentVideoPath = string.Empty;

    [ObservableProperty] private bool                      _isMicEnabled    = true;
    [ObservableProperty] private string                    _selectedMicDevice = string.Empty;
    [ObservableProperty] private IReadOnlyList<string>     _micDevices        = [];
    [ObservableProperty] private ModeProfile?              _selectedMode;
    [ObservableProperty] private IReadOnlyList<ModeProfile> _availableModes   = [];

    private int _currentSessionId;

    public MainViewModel(
        IScreenCaptureService  captureService,
        ILogService            logService,
        IInputMonitorService   inputMonitor,
        IAudioCaptureService   audioCaptureService,
        ModeProfileService     profileService,
        DiskSpaceService       diskSpaceService,
        MediaMergeService      mediaMergeService,
        IGlobalHotkeyService   hotkeyService)
    {
        _captureService      = captureService;
        _logService          = logService;
        _inputMonitor        = inputMonitor;
        _audioCaptureService = audioCaptureService;
        _profileService      = profileService;
        _diskSpaceService    = diskSpaceService;
        _mediaMergeService   = mediaMergeService;
        _hotkeyService       = hotkeyService;

        AvailableModes = _profileService.GetAllProfiles();
        SelectedMode   = AvailableModes.First(m => m.Mode == RecordingMode.Personal);

        MicDevices = _audioCaptureService.GetAvailableDevices();
        if (MicDevices.Count > 0)
            SelectedMicDevice = MicDevices[0];

        _hotkeyService.StartStopRequested += async (_, _) =>
        {
            await App.Current.Dispatcher.InvokeAsync(async () =>
            {
                System.Windows.Application.Current.MainWindow?.Activate();
                System.Windows.Application.Current.MainWindow?.Focus();

                if (IsRecording) await StopRecordingAsync();
                else             await StartRecordingAsync();
            });
        };

        _hotkeyService.MarkerRequested += async (_, _) =>
            await App.Current.Dispatcher.InvokeAsync(async () => await AddMarkerAsync());

        _hotkeyService.ScreenshotRequested += async (_, _) =>
            await App.Current.Dispatcher.InvokeAsync(async () => await TakeScreenshotAsync());

        _captureService.StatusChanged += (_, msg) => StatusText = msg;

        _captureService.CaptureTargetSelected += (_, _) =>
            App.Current.Dispatcher.Invoke(() =>
                ((App)App.Current).GetOverlay().Show());

        _captureService.RecordingStarted += async (_, _) =>
        {
            _logService.AdjustSessionStart(_currentSessionId, DateTime.UtcNow);

            _durationTimer = new System.Timers.Timer(1000);
            _durationTimer.Elapsed += (_, _) =>
                App.Current.Dispatcher.Invoke(() =>
                    RecordingDuration = RecordingDuration.Add(TimeSpan.FromSeconds(1)));
            _durationTimer.Start();

            if (IsMicEnabled && MicDevices.Count > 0)
            {
                try
                {
                    _audioCaptureService.SelectedDevice = SelectedMicDevice;
                    await _audioCaptureService.StartAsync(_currentAudioPath);
                }
                catch (Exception ex)
                {
                    StatusText = "Audio Error: " + ex.Message;
                }
            }
        };

        _captureService.DropStatsChanged += (_, args) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                CurrentFps = args.CurrentFps;

                if (args.TotalDropped == 0)
                {
                    DropWarning    = string.Empty;
                    HasDropWarning = false;
                    return;
                }

                var fpsSuffix = args.CurrentFps < 30 ? $"  |  {args.CurrentFps} fps ↓" : string.Empty;
                DropWarning    = $"⚠️ {args.TotalDropped} frames dropped{fpsSuffix}";
                HasDropWarning = true;

                ((App)App.Current).GetOverlay()
                    .UpdateDropStats(args.TotalDropped, args.CurrentFps);
            });
        };
    }

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        var mode = SelectedMode?.Mode ?? RecordingMode.Personal;

        var timestamp  = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var sessionDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "AlgoReplay", timestamp);

        var videoPath     = Path.Combine(sessionDir, "screen.mp4");
        _currentVideoPath = videoPath;

        var diskCheck = _diskSpaceService.Check(videoPath);
        if (!diskCheck.HasEnoughSpace)
        {
            var dialog = new DiskSpaceWarningDialog(diskCheck, _diskSpaceService);
            if (dialog.ShowDialog() != true) return;
        }

        Directory.CreateDirectory(sessionDir);
        _currentAudioPath = Path.Combine(sessionDir, "audio.wav");

        DropWarning    = string.Empty;
        HasDropWarning = false;
        CurrentFps     = 30;

        App.Current.Dispatcher.Invoke(() =>
            ((App)App.Current).GetOverlay().SetOutputPath(videoPath));

        _currentSessionId = await _logService.StartSessionAsync(
            name: $"Session {DateTime.Now:dd.MM.yyyy HH:mm}",
            mode: mode,
            videoFilePath: videoPath);

        _inputMonitor.Stop();
        _inputMonitor.SetMode(mode);
        _inputMonitor.Start(_currentSessionId);

        _inputMonitor.AddWatchPath(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        _inputMonitor.AddWatchPath(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop));

        var started = await _captureService.StartAsync(videoPath);
        if (!started)
        {
            await _logService.EndSessionAsync(_currentSessionId);
            _inputMonitor.Stop();
            return;
        }

        IsRecording = true;
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        await _captureService.StopAsync();

        if (_audioCaptureService.IsRecording)
            await _audioCaptureService.StopAsync();

        await _logService.EndSessionAsync(_currentSessionId);
        _inputMonitor.Stop();

        IsRecording = false;

        _durationTimer?.Stop();
        _durationTimer?.Dispose();
        _durationTimer = null;

        RecordingDuration = TimeSpan.Zero;
        DropWarning       = string.Empty;
        HasDropWarning    = false;
        CurrentFps        = 30;

        App.Current.Dispatcher.Invoke(() =>
            ((App)App.Current).GetOverlay().Hide());

        await TryMergeOutputAsync();
    }

    private async Task TryMergeOutputAsync()
    {
        var videoPath = _currentVideoPath;
        var audioPath = _currentAudioPath;

        if (!MediaMergeService.CanMerge(videoPath, audioPath))
        {
            StatusText = "Merge skipped: one of the source files is missing or empty.";
            return;
        }

        var dir    = Path.GetDirectoryName(videoPath)!;
        var merged = Path.Combine(dir, "merged.mp4");

        StatusText = "Merging audio + video…";
        var ok = await _mediaMergeService.MergeAsync(videoPath, audioPath, merged);

        if (ok)
        {
            await _logService.UpdateSessionVideoPathAsync(_currentSessionId, merged);
            StatusText = $"Saved: {Path.GetFileName(merged)}";
        }
        else
        {
            StatusText = "Merge failed — check logs. Originals are intact.";
        }
    }

    [RelayCommand]
    private async Task AddMarkerAsync()
    {
        if (!IsRecording) return;

        await App.Current.Dispatcher.InvokeAsync(async () =>
        {
            var dialog = new MarkerDialog();
            if (dialog.ShowDialog() != true) return;

            await _logService.LogEventAsync(
                _currentSessionId,
                EventTypes.ManualMarker,
                dialog.MarkerText);
        });
    }

    [RelayCommand]
    private async Task TakeScreenshotAsync()
    {
        if (!IsRecording) return;

        var bitmap = _captureService.GetLatestFrameAsBitmap();
        if (bitmap is null)
        {
            StatusText = "Скріншот не вдався — кадр ще не отримано";
            return;
        }

        var dir           = System.IO.Path.GetDirectoryName(_currentVideoPath)!;
        var screenshotDir = System.IO.Path.Combine(dir, "screenshots");
        Directory.CreateDirectory(screenshotDir);

        var fileName   = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
        var outputPath = System.IO.Path.Combine(screenshotDir, fileName);

        await App.Current.Dispatcher.InvokeAsync(() =>
        {
            var window = new ScreenshotAnnotationWindow(bitmap, outputPath);
            window.ShowDialog();
        });

        if (File.Exists(outputPath))
        {
            await _logService.LogEventAsync(
                _currentSessionId,
                EventTypes.Screenshot,
                $"Скріншот: {System.IO.Path.GetFileName(outputPath)}",
                metadata: outputPath);

            StatusText = "Скріншот збережено";
        }
    }

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectMode));
        OnPropertyChanged(nameof(CanAddMarker));
    }
}