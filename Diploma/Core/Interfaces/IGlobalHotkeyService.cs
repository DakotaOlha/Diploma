namespace Diploma.Core.Interfaces;

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? StartStopRequested;
    event EventHandler? MarkerRequested;
    event EventHandler? ScreenshotRequested;
    event EventHandler? WhiteboardRequested;

    void Start();
    void Stop();
}