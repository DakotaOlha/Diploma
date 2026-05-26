namespace Diploma.Core.Models;

public static class EventTypes
{
    public const string ClipboardCopy   = "CLIPBOARD_COPY";
    public const string ClipboardPaste  = "CLIPBOARD_PASTE";
    public const string FileSave        = "FILE_SAVE";
    public const string Undo            = "UNDO";
    public const string RunOrDebug      = "RUN_OR_DEBUG";
    public const string IdleStart       = "IDLE_START";
    public const string IdleEnd         = "IDLE_END";
    public const string ManualMarker    = "MANUAL_MARKER";
    public const string Screenshot      = "SCREENSHOT";
    public const string IdeOpened       = "IDE_OPENED";
    public const string IdeClosed       = "IDE_CLOSED";
    public const string FileSwitched    = "FILE_SWITCHED";
    public const string FileSavedAuto   = "FILE_SAVED_AUTO";
    public const string RuleViolation   = "RULE_VIOLATION";
}