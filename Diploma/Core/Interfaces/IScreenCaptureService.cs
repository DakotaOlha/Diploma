namespace Diploma.Core.Interfaces;

public interface IScreenCaptureService
{
    bool IsRecording { get; }
    Task<bool> StartAsync(string outputPath, CancellationToken ct = default);
    Task<string?> TakeScreenshotAsync(string outputDir);
    Task StopAsync();
    event EventHandler<string>? StatusChanged;
    event EventHandler? RecordingStarted;
    event EventHandler? CaptureTargetSelected;
}