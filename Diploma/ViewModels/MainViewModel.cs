using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diploma.Core.Interfaces;

namespace Diploma.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IScreenCaptureService _captureService;

    [ObservableProperty] private bool _isRecording;

    [ObservableProperty] private string _statusText = "Ready";

    [ObservableProperty] private TimeSpan _recordingDuration;
    
    private System.Timers.Timer? _durationTimer;

    public MainViewModel(IScreenCaptureService captureService)
    {
        _captureService = captureService;
        _captureService.StatusChanged += (_, msg) =>
        {
            StatusText = msg;
        };
    }

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        var outputPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "AlgoReplay",
            $"session_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        await _captureService.StartAsync(outputPath);
        IsRecording = true;

        _durationTimer = new System.Timers.Timer(1000);
        _durationTimer.Elapsed += (_, _) =>
        {
            App.Current.Dispatcher.Invoke(() =>
                    RecordingDuration = RecordingDuration.Add(TimeSpan.FromSeconds(1)));
        };
        _durationTimer.Start();
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        await _captureService.StopAsync();
        IsRecording = false;

        _durationTimer?.Stop();
        _durationTimer?.Dispose();
        _durationTimer = null;
        
        RecordingDuration = TimeSpan.Zero;
    }
}