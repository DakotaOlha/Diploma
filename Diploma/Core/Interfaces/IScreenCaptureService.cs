namespace Diploma.Core.Interfaces;

public interface IScreenCaptureService
{
    bool IsRecording { get; }
    Task StartAsync(string outputPath, CancellationToken ct = default);
    Task StopAsync();
    event EventHandler<string>? StatusChanged;
    event EventHandler? RecordingStarted;
    event EventHandler? CaptureTargetSelected;
}