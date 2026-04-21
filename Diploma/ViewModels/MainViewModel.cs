using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using Diploma.Core.Services;

namespace Diploma.ViewModels;

public partial class MainViewModel : ObservableObject
{

    [ObservableProperty] private bool _isRecording;

    [ObservableProperty] private string _statusText = "Ready";

    [ObservableProperty] private TimeSpan _recordingDuration;
    
    private System.Timers.Timer? _durationTimer;
    
    private readonly IScreenCaptureService _captureService;
    private readonly ILogService _logService;
    private readonly IInputMonitorService _inputMonitor;
    private readonly IAudioCaptureService _audioCaptureService;
    
    private string _currentAudioPath = string.Empty;
    
    [ObservableProperty] private bool _isMicEnabled = true;
    [ObservableProperty] private string _selectedMicDevice = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> _micDevices = [];
    
    private int _currentSessionId;

    public MainViewModel(
        IScreenCaptureService captureService, 
        ILogService logService, 
        IInputMonitorService inputMonitor,
        IAudioCaptureService audioCaptureService)
    {
        _captureService = captureService;
        _logService = logService;
        _inputMonitor = inputMonitor;
        _audioCaptureService = audioCaptureService;
        
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
                    await _logService.LogEventAsync(
                        _currentSessionId, "AUDIO_START",
                        $"Microphone recording started: {SelectedMicDevice}");
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
        var timestamp  = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var sessionDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "AlgoReplay", timestamp);

        Directory.CreateDirectory(sessionDir);

        var videoPath = Path.Combine(sessionDir, "screen.mp4");
        _currentAudioPath = Path.Combine(sessionDir, "audio.wav");

        _currentSessionId = await _logService.StartSessionAsync(
            name: $"Session {DateTime.Now:dd.MM.yyyy HH:mm}",
            mode: RecordingMode.Personal,
            videoFilePath: videoPath);

        _inputMonitor.Stop();
        _inputMonitor.Start(_currentSessionId);

        await _captureService.StartAsync(videoPath);

        IsRecording = true;

        await _logService.LogEventAsync(
            _currentSessionId, "RECORDING_START", "Recording started");
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        await _captureService.StopAsync();

        if (_audioCaptureService.IsRecording)
        {
            await _audioCaptureService.StopAsync();
            await _logService.LogEventAsync(
                _currentSessionId, "AUDIO_STOP", "Microphone recording stopped");
        }

        await _logService.LogEventAsync(
            _currentSessionId, "RECORDING_STOP", "Recording stopped");

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

                    await _logService.LogEventAsync(
                        _currentSessionId, "AUDIO_START",
                        $"Microphone recording started: {SelectedMicDevice}");
                }
                catch (Exception ex)
                {
                    StatusText = "Audio Error: " + ex.Message;
                }
            }
        });
    }
}