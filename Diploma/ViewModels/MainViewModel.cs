using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using Diploma.Core.Services;
using Diploma.Views;
using Serilog;

namespace Diploma.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public bool CanSelectMode    => !IsRecording;
    public bool CanAddMarker     => IsRecording;
    public bool CanSelectQuality => !IsRecording;

    [ObservableProperty] private bool     _isRecording;
    [ObservableProperty] private TimeSpan _recordingDuration;
    [ObservableProperty] private bool     _hasDropWarning;
    [ObservableProperty] private string   _statusText        = "Ready";
    [ObservableProperty] private string   _dropWarning       = string.Empty;
    [ObservableProperty] private int      _currentFps        = 30;
    [ObservableProperty] private bool     _isMicEnabled      = true;
    [ObservableProperty] private string   _selectedMicDevice = string.Empty;

    [ObservableProperty] private IReadOnlyList<string>                _micDevices          = [];
    [ObservableProperty] private ModeProfile?                         _selectedMode;
    [ObservableProperty] private IReadOnlyList<ModeProfile>           _availableModes      = [];
    [ObservableProperty] private CaptureQualityProfile?               _selectedQuality;
    [ObservableProperty] private IReadOnlyList<CaptureQualityProfile> _availableQualities  = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand), nameof(StopRecordingCommand))]
    private bool _isBusy;

    private readonly IScreenCaptureService  _captureService;
    private readonly ILogService            _logService;
    private readonly IInputMonitorService   _inputMonitor;
    private readonly IAudioCaptureService   _audioCaptureService;
    private readonly ModeProfileService     _profileService;
    private readonly DiskSpaceService       _diskSpaceService;
    private readonly MediaMergeService      _mediaMergeService;
    private readonly IGlobalHotkeyService   _hotkeyService;

    private readonly SemaphoreSlim _recordingLock = new(1, 1);
    private CancellationTokenSource? _startCts;

    private int      _currentSessionId;
    private string   _currentAudioPath  = string.Empty;
    private string   _currentVideoPath  = string.Empty;
    private string   _currentSessionDir = string.Empty;
    private DateTime _recordingStartedAt;

    private System.Windows.Threading.DispatcherTimer? _durationTimer;

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

        AvailableQualities = new[]
        {
            CaptureQualityProfile.Low,
            CaptureQualityProfile.Medium,
            CaptureQualityProfile.High,
        };
        SelectedQuality = CaptureQualityProfile.Medium;

        MicDevices = _audioCaptureService.GetAvailableDevices();
        if (MicDevices.Count > 0)
            SelectedMicDevice = MicDevices[0];

        WireEvents();
    }

    private void WireEvents()
    {
        _hotkeyService.StartStopRequested += async (_, _) =>
            await App.Current.Dispatcher.InvokeAsync(async () =>
            {
                App.Current.MainWindow?.Activate();
                App.Current.MainWindow?.Focus();
                if (IsRecording) await StopRecordingAsync();
                else             await StartRecordingAsync();
            });

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
            var realStart = DateTime.UtcNow;
            _recordingStartedAt = realStart;
            _logService.AdjustSessionStart(_currentSessionId, realStart);

            _inputMonitor.SetMode(SelectedMode?.Mode ?? RecordingMode.Personal);
            _inputMonitor.Start(_currentSessionId);

            StartDurationTimer(realStart);

            if (IsMicEnabled && MicDevices.Count > 0)
            {
                try
                {
                    _audioCaptureService.SelectedDevice = SelectedMicDevice;
                    await _audioCaptureService.StartAsync(_currentAudioPath);
                }
                catch (Exception ex)
                {
                    StatusText = "Audio error: " + ex.Message;
                }
            }
        };

        _captureService.DropStatsChanged += (_, args) =>
            App.Current.Dispatcher.Invoke(() =>
            {
                CurrentFps = args.CurrentFps;

                if (args.TotalDropped == 0)
                {
                    DropWarning    = string.Empty;
                    HasDropWarning = false;
                    return;
                }

                var fpsSuffix = args.CurrentFps < (SelectedQuality?.Fps ?? 30)
                    ? $"  |  {args.CurrentFps} fps ↓"
                    : string.Empty;

                DropWarning    = $"⚠️ {args.TotalDropped} frames dropped{fpsSuffix}";
                HasDropWarning = true;

                ((App)App.Current).GetOverlay()
                    .UpdateDropStats(args.TotalDropped, args.CurrentFps);
            });
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartRecordingAsync()
    {
        if (!await _recordingLock.WaitAsync(0)) return;

        var cts = new CancellationTokenSource();
        _startCts = cts;

        try
        {
            if (IsRecording || IsBusy) return;

            IsBusy     = true;
            StatusText = "Ініціалізація запису...";

            if (_captureService is ScreenCaptureService scs && SelectedQuality is not null)
                scs.Quality = SelectedQuality.Quality;

            var videoDir  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "AlgoReplay");
            var diskCheck = _diskSpaceService.Check(videoDir);
            if (!diskCheck.HasEnoughSpace)
            {
                StatusText = $"⚠️ Мало місця на диску: " +
                             $"{_diskSpaceService.FormatFreeSpace(diskCheck.FreeBytes)} вільно " +
                             $"(~{diskCheck.EstimatedHours:F1} год)";
                return;
            }

            var timestamp  = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var sessionDir = Path.Combine(videoDir, timestamp);
            Directory.CreateDirectory(sessionDir);

            var videoPath = Path.Combine(sessionDir, "screen.mp4");
            var audioPath = Path.Combine(sessionDir, "audio.wav");

            StatusText = "Вибір вікна запису...";
            await _captureService.PrepareAsync(videoPath, cts.Token);

            _currentVideoPath  = videoPath;
            _currentAudioPath  = audioPath;
            _currentSessionDir = sessionDir;

            _currentSessionId = await _logService.StartSessionAsync(
                $"Session {DateTime.Now:dd.MM HH:mm}",
                SelectedMode?.Mode ?? RecordingMode.Personal,
                _currentVideoPath);

            StatusText = "Запуск захоплення...";
            await _captureService.BeginCaptureAsync(cts.Token);

            if (cts.Token.IsCancellationRequested)
            {
                await RollbackStartAsync(_currentSessionId, _currentSessionDir);
                return;
            }

            IsRecording = true;
            StatusText  = "Запис триває...";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Запис скасовано.";
            await RollbackStartAsync(_currentSessionId, _currentSessionDir);
        }
        catch (Exception ex)
        {
            StatusText = $"Помилка старту: {ex.Message}";
            await RollbackStartAsync(_currentSessionId, _currentSessionDir);
        }
        finally
        {
            IsBusy    = false;
            _startCts = null;
            cts.Dispose();
            _recordingLock.Release();
            NotifyCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopRecordingAsync()
    {
        _startCts?.Cancel();
        if (!await _recordingLock.WaitAsync(0)) return;

        var sessionId = _currentSessionId;
        var videoPath = _currentVideoPath;
        var audioPath = _currentAudioPath;

        try
        {
            if (!IsRecording || IsBusy) return;
            IsBusy = true;
            IsRecording = false;
            ResetDurationCounter();
            StatusText = "Зупинка та збереження...";
            NotifyCommands();

            _inputMonitor.Stop();

            if (_audioCaptureService.IsRecording)
                await _audioCaptureService.StopAsync();

            await _captureService.StopAsync();

            await _logService.EndSessionAsync(sessionId);
            await ProcessMergeAsync(videoPath, audioPath, sessionId);
        }
        finally
        {
            IsBusy = false;
            _recordingLock.Release();
            NotifyCommands();
        }
    }

    private async Task ProcessMergeAsync(string videoPath, string audioPath, int sessionId)
    {
        if (!MediaMergeService.IsMp4Valid(videoPath))
        {
            Log.Warning("ProcessMerge: video file invalid — {Path}", videoPath);
            StatusText = "⚠️ Відеофайл пошкоджено (немає moov atom). Відео збережено без аудіо.";
            await _logService.UpdateSessionVideoPathAsync(sessionId, videoPath);
            return;
        }

        bool hasAudio = File.Exists(audioPath) && new FileInfo(audioPath).Length > 4_096;

        if (!hasAudio)
        {
            StatusText = "✅ Збережено (без аудіо).";
            await _logService.UpdateSessionVideoPathAsync(sessionId, videoPath);
            return;
        }

        var dir    = Path.GetDirectoryName(videoPath)!;
        var merged = Path.Combine(dir, "merged.mp4");

        StatusText = "Обробка відео та аудіо...";

        var ok = await _mediaMergeService.MergeAsync(videoPath, audioPath, merged);

        if (ok)
        {
            await _logService.UpdateSessionVideoPathAsync(sessionId, merged);

            TryDeleteFile(videoPath);
            TryDeleteFile(audioPath);

            StatusText = $"✅ Збережено: {Path.GetFileName(merged)}";
        }
        else
        {
            await _logService.UpdateSessionVideoPathAsync(sessionId, videoPath);
            TryDeleteFile(audioPath);
            StatusText = "⚠️ Merge не вдався. Відео без аудіо збережено.";
        }
    }

    private void StartDurationTimer(DateTime startedAt)
    {
        StopDurationTimer();

        _recordingStartedAt = startedAt;

        _durationTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            App.Current.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _durationTimer.Tick += (_, _) =>
            RecordingDuration = DateTime.UtcNow - _recordingStartedAt;
        _durationTimer.Start();
    }

    private void ResetDurationCounter()
    {
        StopDurationTimer();
        RecordingDuration = TimeSpan.Zero;
    }

    private void StopDurationTimer()
    {
        if (_durationTimer is null) return;
        _durationTimer.Stop();
        _durationTimer = null;
    }

    private async Task RollbackStartAsync(int sessionId, string? sessionDir = null)
    {
        try { _inputMonitor.Stop(); } catch { }
        try { await _captureService.StopAsync(); } catch { }

        if (_audioCaptureService.IsRecording)
            try { await _audioCaptureService.StopAsync(); } catch { }

        if (sessionId > 0)
            try { await _logService.DeleteSessionAsync(sessionId); } catch { }

        if (!string.IsNullOrEmpty(sessionDir) && Directory.Exists(sessionDir))
            try { Directory.Delete(sessionDir, recursive: true); } catch { }

        _currentSessionId  = 0;
        _currentSessionDir = string.Empty;
        _currentVideoPath  = string.Empty;
        _currentAudioPath  = string.Empty;
        ResetDurationCounter();
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
                _currentSessionId, EventTypes.ManualMarker, dialog.MarkerText);
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

        var dir           = Path.GetDirectoryName(_currentVideoPath)!;
        var screenshotDir = Path.Combine(dir, "screenshots");
        Directory.CreateDirectory(screenshotDir);

        var ts         = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var outputPath = Path.Combine(screenshotDir, $"screenshot_{ts}.png");

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
                $"Скріншот: {Path.GetFileName(outputPath)}",
                metadata: outputPath);

            StatusText = "Скріншот збережено";
        }
    }

    private void NotifyCommands()
    {
        StartRecordingCommand.NotifyCanExecuteChanged();
        StopRecordingCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectMode));
        OnPropertyChanged(nameof(CanAddMarker));
        OnPropertyChanged(nameof(CanSelectQuality));
    }

    private bool CanStart() => !IsRecording && !IsBusy;
    private bool CanStop()  => IsRecording  && !IsBusy;

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}