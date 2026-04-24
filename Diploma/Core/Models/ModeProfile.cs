namespace Diploma.Core.Models;

public class ModeProfile
{
    public RecordingMode Mode { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCustomizable { get; set; }
    public HashSet<string> AllowedEvents { get; set; } = new();
    
    public bool IsAllowed(string eventType) => AllowedEvents.Contains(eventType);
}