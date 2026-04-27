using System.Runtime.InteropServices;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;

namespace Diploma.Core.Services;

public class WindowTitleMonitor : IDisposable
{
    private static readonly HashSet<string> IdeProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "rider", "devenv", "code", "idea", "clion",
        "pycharm", "webstorm", "goland", "fleet"
    };
    
    private readonly ILogService _logService;
    private Timer? _timer;

    private string _lastTitle = string.Empty;
    private string _lastProcess = string.Empty;
    private int _sessionId;
    private bool _isRunning;
    private bool _disposed;
    
    public WindowTitleMonitor(ILogService logService)
    {
        _logService = logService;
    }

    public void Start(int sessionId)
    {
        if (_isRunning) return;
        
        _sessionId = sessionId;
        _isRunning = true;
        _lastTitle = string.Empty;
        _lastProcess = string.Empty;

        _timer = new Timer(Tick, null, 0, 1000);
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _timer?.Dispose();
        _timer = null;
    }

    public void Tick(object? state)
    {
        if (!_isRunning) return;

        try
        {
            var hwmd = GetForegroundWindow();
            if (hwmd == IntPtr.Zero) return;
            
            var title = GetWindowTitle(hwmd);
            var process = GetProcessName(hwmd);

            if (string.IsNullOrWhiteSpace(title)) return;

            if (title == _lastTitle && process == _lastProcess) return;
            
            var isIde = IdeProcessNames.Contains(process);

            if (isIde && title != _lastTitle)
            {
                var fileName = ExtractFileName(title, process);

                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    _ = _logService.LogEventAsync(
                        _sessionId,
                        EventTypes.FileSwitched,
                        $"Switched to: {fileName}",
                        metadata: process);
                }
            }
            
            _lastTitle = title;
            _lastProcess = process;
        }
        catch {}
    }

    private static string ExtractFileName(string title, string process)
    {
        var separations = new[] { " – ", " — ", " - " };

        foreach (var sep in separations)
        {
            var idx = title.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
                return title[..idx].Trim();
        }
        
        return title.Trim();
    }
    
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
    
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(hWnd, sb, 256);
        return sb.ToString();
    }

    private static string GetProcessName(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var pid);
        try
        {
            return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}