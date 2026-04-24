using Diploma.Core.Models;
using System.Windows.Forms;

namespace Diploma.Core.Interfaces;

public interface IInputProcessorService
{
    void SetMode(RecordingMode mode);
    Task ProcessShortcutAsync(int sessionId, Keys key, bool ctrl, bool shift);
}