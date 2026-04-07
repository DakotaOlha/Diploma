using Diploma.Core.Models;

namespace Diploma.Core.Interfaces;

public interface ILogService
{
    Task<int> StartSessionAsync(string name, RecordingMode mode, string videoFilePath);
    Task EndSessionAsync(int sessionId);
    Task<RecordingSession?> GetSessionAsync(int sessionId);
    Task<IReadOnlyList<RecordingSession>> GetAllSessionsAsync();

    Task LogEventAsync(int sessionId, string eventType,
        string description, string? metadata = null);
    Task<IReadOnlyList<LogEntry>> GetEntriesAsync(int sessionId);
    Task<IReadOnlyList<LogEntry>> GetEntriesByTypeAsync(int sessionId, string eventType);
}