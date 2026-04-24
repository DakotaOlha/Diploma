using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using Diploma.Helpers;
using System.Windows.Forms;

namespace Diploma.Core.Services;

public class InputProcessorService : IInputProcessorService
{
    private readonly ILogService _logService;
    private readonly ModeProfileService _profileService;
    private ModeProfile _currentProfile;

    public InputProcessorService(ILogService logService, ModeProfileService profileService)
    {
        _logService      = logService;
        _profileService  = profileService;
        _currentProfile  = profileService.GetProfile(RecordingMode.Personal);
    }

    public void SetMode(RecordingMode mode)
    {
        _currentProfile = _profileService.GetProfile(mode);
    }

    public async Task ProcessShortcutAsync(int sessionId, Keys key, bool ctrl, bool shift)
    {
        if (!ctrl && key != Keys.F5) return;

        switch (key)
        {
            case Keys.Z:
                await LogIfAllowed(sessionId, EventTypes.Undo, "Undo action triggered");
                break;

            case Keys.C:
                await LogIfAllowed(sessionId, EventTypes.ClipboardCopy, "User copied data");
                break;

            case Keys.V:
                await LogIfAllowed(sessionId, EventTypes.ClipboardPaste, "User pasted data");
                break;

            case Keys.S:
                var title = WindowHelper.GetActiveWindowTitle();
                await LogIfAllowed(sessionId, EventTypes.FileSave, "File saved", title);
                break;

            case Keys.F5:
                var action = ctrl
                    ? "Start without debugging (Ctrl+F5)"
                    : "Start debugging (F5)";
                await LogIfAllowed(sessionId, EventTypes.RunOrDebug, action);
                break;
        }
    }

    private async Task LogIfAllowed(int sessionId, string eventType,
                                     string description, string? metadata = null)
    {
        if (_currentProfile.IsAllowed(eventType))
            await _logService.LogEventAsync(sessionId, eventType, description, metadata);
    }
}