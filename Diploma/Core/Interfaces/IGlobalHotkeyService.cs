namespace Diploma.Core.Interfaces;

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? StartStopRequested;
    event EventHandler? MarkerRequested;
    
    void Start();
    void Stop();
}