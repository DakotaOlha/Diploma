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
    
    private int _currentSessionId;

    public MainViewModel(
        IScreenCaptureService captureService, 
        ILogService logService, 
        IInputMonitorService inputMonitor)
    {
        _captureService = captureService;
        _logService = logService;
        _inputMonitor = inputMonitor;
        
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
        _captureService.RecordingStarted += OnRecordingStarted;
    }

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        var outputPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "AlgoReplay",
            $"session_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        _currentSessionId = await _logService.StartSessionAsync(
            name: $"Session {DateTime.Now:dd.MM.yyyy HH:mm}",
            mode: RecordingMode.Personal,
            videoFilePath: outputPath);

        _inputMonitor.Stop();
        _inputMonitor.Start(_currentSessionId);
        
        await _captureService.StartAsync(outputPath);
        IsRecording = true;

        await _logService.LogEventAsync(
            _currentSessionId, "RECORDING_START", "Recording started");
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        await _captureService.StopAsync();

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
    }
    
    private void OnRecordingStarted(object? sender, EventArgs e)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            _durationTimer = new System.Timers.Timer(1000);
            _durationTimer.Elapsed += (_, _) =>
                App.Current.Dispatcher.Invoke(() =>
                    RecordingDuration = RecordingDuration.Add(TimeSpan.FromSeconds(1)));
            _durationTimer.Start();
        });
    }
}