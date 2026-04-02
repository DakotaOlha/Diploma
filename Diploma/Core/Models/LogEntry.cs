namespace Diploma.Core.Models;

public class LogEntry
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public DateTime Timestamp { get; set; }
    public TimeSpan Offset { get; set; }      // time since the start of recording
    public string EventType { get; set; }     // "FILE_SAVE", "IDE_OPEN", etc
    public string Description { get; set; }
    public string? Metadata { get; set; } 
}