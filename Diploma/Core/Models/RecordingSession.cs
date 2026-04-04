namespace Diploma.Core.Models;

public class RecordingSession
{
    public int Id { get; set; }
    public string Name { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string Mode { get; set; }
    public string VideoFilePath { get; set; }
}