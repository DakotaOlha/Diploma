using System.Runtime.InteropServices;
using System.Text;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;

namespace Diploma.Core.Services;

public sealed class BlacklistMonitor : IDisposable
{
    private static readonly string[] Terms =
    [
        "telegram", "discord", "slack", "whatsapp", "viber", "skype",
        "chatgpt", "chat.openai", "claude.ai",
        "gemini", "bard",
        "copilot", "github copilot",
        "deepseek",
        "perplexity", "grok",
    ];

    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private readonly ILogService _logService;
    private Timer?  _timer;
    private int     _sessionId;
    private bool    _isRunning;
    private bool    _disposed;

    private string   _lastTitle = string.Empty;
    private DateTime _lastLogAt = DateTime.MinValue;

    public event EventHandler<string>? ViolationDetected;

    public BlacklistMonitor(ILogService logService) => _logService = logService;

    public void Start(int sessionId)
    {
        if (_isRunning) return;
        _sessionId  = sessionId;
        _isRunning  = true;
        _lastTitle  = string.Empty;
        _lastLogAt  = DateTime.MinValue;
        _timer      = new Timer(Tick, null, 0, 1_000);
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _timer?.Dispose();
        _timer = null;
    }

    private void Tick(object? _)
    {
        if (!_isRunning) return;
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return;

            var lower   = title.ToLowerInvariant();
            var matched = Array.Find(Terms, t => lower.Contains(t));
            if (matched is null) return;

            var now = DateTime.UtcNow;

            if (title == _lastTitle && now - _lastLogAt < Cooldown) return;

            _lastTitle = title;
            _lastLogAt = now;

            ViolationDetected?.Invoke(this, $"Порушення: {matched}");

            _ = _logService.LogEventAsync(
                _sessionId,
                EventTypes.RuleViolation,
                $"Порушення: {title}",
                metadata: matched);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
