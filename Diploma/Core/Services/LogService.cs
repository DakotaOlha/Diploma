using Diploma.Core.Interfaces;

namespace Diploma.Core.Services;

public class LogService : ILogService
{
    public Task LogEventAsync(string eventType, string description)
    {
        // заглушка
        return Task.CompletedTask;
    }
}