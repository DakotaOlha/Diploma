using Diploma.Core.Models;

namespace Diploma.Core.Services;

public class ModeProfileService
{
    private readonly Dictionary<RecordingMode, ModeProfile> _profiles;

    public ModeProfileService()
    {
        _profiles = new Dictionary<RecordingMode, ModeProfile>
        {
            [RecordingMode.Olympic] = new()
            {
                Mode           = RecordingMode.Olympic,
                DisplayName    = "🏆 Олімпіада",
                Description    = "Суворий режим для ICPC та подібних змагань",
                IsCustomizable = false,
                AllowedEvents  = new HashSet<string>
                {
                    EventTypes.ClipboardCopy,
                    EventTypes.ClipboardPaste,
                    EventTypes.FileSave,
                    EventTypes.RunOrDebug,
                    EventTypes.IdleStart,
                    EventTypes.IdleEnd,
                    EventTypes.ManualMarker,
                    EventTypes.Screenshot,
                }
            },

            [RecordingMode.Learning] = new()
            {
                Mode           = RecordingMode.Learning,
                DisplayName    = "📚 Навчання",
                Description    = "Для навчальних сесій та туторіалів",
                IsCustomizable = true,
                AllowedEvents  = new HashSet<string>
                {
                    EventTypes.ClipboardCopy,
                    EventTypes.ClipboardPaste,
                    EventTypes.FileSave,
                    EventTypes.Undo,
                    EventTypes.RunOrDebug,
                    EventTypes.IdleStart,
                    EventTypes.IdleEnd,
                    EventTypes.ManualMarker,
                    EventTypes.Screenshot,
                }
            },

            [RecordingMode.Work] = new()
            {
                Mode           = RecordingMode.Work,
                DisplayName    = "💼 Робота",
                Description    = "Для робочих сесій та код рев'ю",
                IsCustomizable = true,
                AllowedEvents  = new HashSet<string>
                {
                    EventTypes.ClipboardCopy,
                    EventTypes.ClipboardPaste,
                    EventTypes.FileSave,
                    EventTypes.RunOrDebug,
                    EventTypes.ManualMarker,
                    EventTypes.Screenshot,
                }
            },

            [RecordingMode.Personal] = new()
            {
                Mode           = RecordingMode.Personal,
                DisplayName    = "🙂 Особистий",
                Description    = "Вільний режим, всі події записуються",
                IsCustomizable = true,
                AllowedEvents  = new HashSet<string>
                {
                    EventTypes.ClipboardCopy,
                    EventTypes.ClipboardPaste,
                    EventTypes.FileSave,
                    EventTypes.RunOrDebug,
                    EventTypes.IdleStart,
                    EventTypes.IdleEnd,
                    EventTypes.ManualMarker,
                    EventTypes.Screenshot,
                    EventTypes.IdeOpened, 
                    EventTypes.IdeClosed,
                    EventTypes.FileSwitched,
                    EventTypes.FileSavedAuto,
                }
            },
        };
    }

    public ModeProfile GetProfile(RecordingMode mode) => _profiles[mode];

    public IReadOnlyList<ModeProfile> GetAllProfiles() =>
        _profiles.Values.ToList();

    public void UpdateProfile(ModeProfile updated)
    {
        if (!updated.IsCustomizable)
            throw new InvalidOperationException(
                $"Profile '{updated.DisplayName}' cannot be modified");

        _profiles[updated.Mode] = updated;
    }
}