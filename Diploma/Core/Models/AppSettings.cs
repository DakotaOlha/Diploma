using Diploma.Core.Services;

namespace Diploma.Core.Models;

public class AppSettings
{
    public CaptureQuality DefaultQuality { get; set; } = CaptureQuality.Medium;
    public RecordingMode  DefaultMode    { get; set; } = RecordingMode.Personal;

    public bool OlympicShowViolationToast { get; set; } = true;

    public HashSet<string> LearningEvents { get; set; } = [..DefaultLearningEvents];
    public HashSet<string> WorkEvents     { get; set; } = [..DefaultWorkEvents];
    public HashSet<string> PersonalEvents { get; set; } = [..DefaultPersonalEvents];

    public static readonly HashSet<string> DefaultLearningEvents =
    [
        EventTypes.ClipboardCopy, EventTypes.ClipboardPaste,
        EventTypes.FileSave, EventTypes.Undo, EventTypes.RunOrDebug,
        EventTypes.IdleStart, EventTypes.IdleEnd,
        EventTypes.ManualMarker, EventTypes.Screenshot,
    ];

    public static readonly HashSet<string> DefaultWorkEvents =
    [
        EventTypes.ClipboardCopy, EventTypes.ClipboardPaste,
        EventTypes.FileSave, EventTypes.RunOrDebug,
        EventTypes.ManualMarker, EventTypes.Screenshot,
    ];

    public static readonly HashSet<string> DefaultPersonalEvents =
    [
        EventTypes.ClipboardCopy, EventTypes.ClipboardPaste,
        EventTypes.FileSave, EventTypes.RunOrDebug,
        EventTypes.IdleStart, EventTypes.IdleEnd,
        EventTypes.ManualMarker, EventTypes.Screenshot,
        EventTypes.IdeOpened, EventTypes.IdeClosed,
        EventTypes.FileSwitched, EventTypes.FileSavedAuto,
    ];
}
