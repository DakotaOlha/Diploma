using System.Windows.Media.Imaging;
using Diploma.Core.Services;

namespace Diploma.Core.Interfaces;

public interface IScreenCaptureService
{
    bool IsRecording { get; }
    Task PrepareAsync(string outputPath, CancellationToken ct = default);
    Task BeginCaptureAsync(CancellationToken ct = default);
    Task StopAsync();
    Task<string?> TakeScreenshotAsync(string outputDir);
    BitmapSource? GetLatestFrameAsBitmap();

    event EventHandler<string>?             StatusChanged;
    event EventHandler?                     RecordingStarted;      
    event EventHandler?                     CaptureTargetSelected; 
    event EventHandler<DropStatsEventArgs>? DropStatsChanged;
}