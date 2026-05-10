using System.Windows.Media.Imaging;
using Diploma.Core.Services;

namespace Diploma.Core.Interfaces;

public interface IScreenCaptureService
{
    bool IsRecording { get; }

    Task<bool> StartAsync(string outputPath, CancellationToken ct = default);
    Task<string?> TakeScreenshotAsync(string outputDir);
    Task StopAsync();

    BitmapSource? GetLatestFrameAsBitmap();

    event EventHandler<string>? StatusChanged;
    event EventHandler? RecordingStarted;
    event EventHandler? CaptureTargetSelected;
    event EventHandler<DropStatsEventArgs>? DropStatsChanged;
}