namespace Diploma.Core.Interfaces;

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? StartStopRequested;
    event EventHandler? MarkerRequested;
    event EventHandler? ScreenshotRequested;
    
    void Start();
    void Stop();
}