using Diploma.Core.Interfaces;
using System.Windows.Forms;

namespace Diploma.Core.Services;

public class InputProcessorService : IInputProcessorService
{
    private readonly ILogService _logService;

    public InputProcessorService(ILogService logService)
    {
        _logService = logService;
    }

    public async Task ProcessShortcutAsync(int sessionId, Keys key, bool ctrl, bool shift)
    {
        if (!ctrl && key != Keys.F5) return;

        switch (key)
        {
            case Keys.Z:
                await _logService.LogEventAsync(sessionId, "UNDO", "Undo action triggered");
                break;
            case Keys.C:
                await _logService.LogEventAsync(sessionId, "CLIPBOARD_COPY", "User copied data");
                break;
            case Keys.V:
                await _logService.LogEventAsync(sessionId, "CLIPBOARD_PASTE", "User pasted data");
                break;
            case Keys.S:
                var title = Helpers.WindowHelper.GetActiveWindowTitle();
                await _logService.LogEventAsync(sessionId, "FILE_SAVE", "File saved", title);
                break;
            case Keys.F5:
                string action = ctrl ? "Start without debugging (Ctrl+F5)" : "Start debugging (F5)";
                await _logService.LogEventAsync(sessionId, "RUN_OR_DEBUG", action);
                break;
        }
    }
}