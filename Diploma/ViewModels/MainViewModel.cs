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
    public bool CanAddMarker => IsRecording;

    [ObservableProperty] private bool _isRecording;

    [ObservableProperty] private string _statusText = "Ready";

    [ObservableProperty] private TimeSpan _recordingDuration;
    
    private System.Timers.Timer? _durationTimer;
    
    private readonly IScreenCaptureService _captureService;
    private readonly ILogService _logService;
    private readonly IInputMonitorService _inputMonitor;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly ModeProfileService _profileService;
    private readonly DiskSpaceService _diskSpaceService;
    
    private string _currentAudioPath = string.Empty;
    
    [ObservableProperty] private bool _isMicEnabled = true;
    [ObservableProperty] private string _selectedMicDevice = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> _micDevices = [];
    [ObservableProperty] private ModeProfile? _selectedMode;
    [ObservableProperty] private IReadOnlyList<ModeProfile> _availableModes = [];
    
    private int _currentSessionId;

    public MainViewModel(
        IScreenCaptureService captureService, 
        ILogService logService, 
        IInputMonitorService inputMonitor,
        IAudioCaptureService audioCaptureService,
        ModeProfileService profileService,
        DiskSpaceService diskSpaceService)
    {
        _captureService = captureService;
        _logService = logService;
        _inputMonitor = inputMonitor;
        _audioCaptureService = audioCaptureService;
        _profileService = profileService;
        _diskSpaceService = diskSpaceService;
        
        AvailableModes = _profileService.GetAllProfiles();
        SelectedMode = AvailableModes.First(m => m.Mode == RecordingMode.Personal);
        
        MicDevices = _audioCaptureService.GetAvailableDevices();
            if (MicDevices.Count > 0)
                SelectedMicDevice = MicDevices[0];
        
        if (_inputMonitor is InputMonitorService monitor)
        {
            monitor.HotkeyStartStop += async (_, _) =>
            {
                await App.Current.Dispatcher.InvokeAsync(async () =>
                {
                    if (IsRecording)
                        await StopRecordingAsync();
                    else
                        await StartRecordingAsync();
                });
            };
            
            monitor.HotkeyMarker += async (_, _) =>
            {
                await App.Current.Dispatcher.InvokeAsync(async () =>
                    await AddMarkerAsync());
            };
        
            monitor.Start(0);
        }
        
        _captureService.StatusChanged  += (_, msg) => StatusText = msg;
        
        _captureService.CaptureTargetSelected += (_, _) =>
        {
            App.Current.Dispatcher.Invoke(() =>
                ((App)App.Current).GetOverlay().Show());
        };
        
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
    }

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        var mode = SelectedMode?.Mode ?? RecordingMode.Personal;
        
        var timestamp  = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var sessionDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "AlgoReplay", timestamp);
        
        var videoPath = Path.Combine(sessionDir, "screen.mp4");
        
        var diskCheck = _diskSpaceService.Check(videoPath);
        if (!diskCheck.HasEnoughSpace)
        {
            var dialog = new DiskSpaceWarningDialog(diskCheck, _diskSpaceService);
            if (dialog.ShowDialog() != true)
                return;
        }
        
        Directory.CreateDirectory(sessionDir);
        
        _currentAudioPath = Path.Combine(sessionDir, "audio.wav");

        App.Current.Dispatcher.Invoke(() =>
            ((App)App.Current).GetOverlay().SetOutputPath(videoPath));
        
        _currentSessionId = await _logService.StartSessionAsync(
            name: $"Session {DateTime.Now:dd.MM.yyyy HH:mm}",
            mode: mode,
            videoFilePath: videoPath);

        _inputMonitor.Stop();
        _inputMonitor.SetMode(mode); 
        _inputMonitor.Start(_currentSessionId);

        await _captureService.StartAsync(videoPath);
        IsRecording = true;
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        await _captureService.StopAsync();

        if (_audioCaptureService.IsRecording)
        {
            await _audioCaptureService.StopAsync();
        }

        await _logService.EndSessionAsync(_currentSessionId);

        _inputMonitor.Stop();
        _inputMonitor.Start(0);

        IsRecording = false;
        _durationTimer?.Stop();
        _durationTimer?.Dispose();
        _durationTimer = null;
        RecordingDuration = TimeSpan.Zero;
        App.Current.Dispatcher.Invoke(() =>
            ((App)App.Current).GetOverlay().Hide());
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
    
    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectMode));
        OnPropertyChanged(nameof(CanAddMarker));
    }
    
    private async void OnRecordingStarted(object? sender, EventArgs e)
    {
        await App.Current.Dispatcher.InvokeAsync(async () =>
        {
            _durationTimer?.Stop(); 
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
        });
    }
}