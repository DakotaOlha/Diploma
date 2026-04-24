using Diploma.Core.Models;

namespace Diploma.Core.Interfaces;

public interface IInputMonitorService : IDisposable
{
    bool IsRunning { get; }
    void Start(int sessionId);
    void Stop();
    void SetMode(RecordingMode mode);
    event EventHandler? HotkeyMarker;
}