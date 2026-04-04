namespace Diploma.Core.Interfaces;

public interface ILogService
{
    Task LogEventAsync(string eventType, string description);
}