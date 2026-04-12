namespace Diploma.Core.Interfaces;

public interface IAudioCaptureService : IDisposable
{
    bool IsRecording { get; }
    Task StartAsync(string outputPath, CancellationToken ct = default);
    Task StopAsync();
    IReadOnlyList<string> GetAvailableDevices();
    string? SelectedDevice { get; set; }
}