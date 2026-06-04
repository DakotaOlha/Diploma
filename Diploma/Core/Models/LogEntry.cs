namespace Diploma.Core.Models;

public class LogEntry
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public DateTime Timestamp { get; set; }
    public TimeSpan Offset { get; set; }
    public string EventType { get; set; }
    public string Description { get; set; }
    public string? Metadata { get; set; } 
}