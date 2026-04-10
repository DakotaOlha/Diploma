namespace Diploma.Core.Interfaces;

public interface IInputMonitorService : IDisposable
{
    bool IsRunning { get; }
    void Start(int sessionId);
    void Stop();
}