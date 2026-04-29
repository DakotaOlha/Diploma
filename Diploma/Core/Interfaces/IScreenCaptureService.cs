namespace Diploma.Core.Interfaces;

public interface IScreenCaptureService
{
    bool IsRecording { get; }
    Task<bool> StartAsync(string outputPath, CancellationToken ct = default); // Змінено на Task<bool>
    Task StopAsync();
    event EventHandler<string>? StatusChanged;
    event EventHandler? RecordingStarted;
    event EventHandler? CaptureTargetSelected;
}