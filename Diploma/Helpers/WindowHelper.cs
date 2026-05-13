using System.Runtime.InteropServices;

namespace Diploma.Helpers;

public static class WindowHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    
    public static string GetActiveWindowTitle()
    {
        IntPtr handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
            return string.Empty;

        var sb = new System.Text.StringBuilder(256);
        GetWindowText(handle, sb, 256);
        return sb.ToString();
    }

    public static int GetActiveWindowProcessId()
    {
        IntPtr handle = GetForegroundWindow();

        if (handle == IntPtr.Zero)
            return 0;

        uint pid;
        uint threadId = GetWindowThreadProcessId(handle, out pid);

        if (threadId == 0)
            return 0;

        return (int)pid;
    }
}