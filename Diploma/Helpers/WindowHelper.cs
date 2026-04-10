using System.Runtime.InteropServices;

namespace Diploma.Helpers;

public static class WindowHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    public static string GetActiveWindowTitle()
    {
        var sb = new System.Text.StringBuilder(256);
        IntPtr handle = GetForegroundWindow();
        if (handle != IntPtr.Zero)
        {
            GetWindowText(handle, sb, 256);
        }
        return sb.ToString();
    }
}