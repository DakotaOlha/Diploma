using System.Windows.Forms;

namespace Diploma.Core.Interfaces;

public interface IInputProcessorService
{
    Task ProcessShortcutAsync(int sessionId, Keys key, bool ctrl, bool shift);
}