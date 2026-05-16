using System.IO;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;

namespace Diploma.Core.Services;

public class FileSystemMonitor : IDisposable
{
    private static readonly HashSet<string> WatchedExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".cpp", ".c", ".h", ".java", ".py",
        ".js", ".ts", ".go", ".rs", ".kt", ".swift",
        ".txt", ".md", ".json", ".xml", ".yaml", ".yml"
    };
    
    private readonly ILogService _logService;
    private readonly List<FileSystemWatcher> _watchers = new();

    private readonly Dictionary<string, DateTime> _lastLogged = new();
    private readonly TimeSpan _debounce = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan CleanupInterval  = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StaleEntryMaxAge = TimeSpan.FromMinutes(10);
    private Timer? _cleanupTimer;

    private int _sessionId;
    private bool _isRunning;
    private bool _disposed;

    public FileSystemMonitor(ILogService logService)
    {
        _logService = logService;
    }

    public void Start(int sessionId, IEnumerable<string> watchPaths)
    {
        if (_isRunning) return;
        _sessionId = sessionId;
        _isRunning = true;

        foreach (var path in watchPaths)
        {
            if (!Directory.Exists(path)) continue;
            AddWatcher(path);
        }

        _cleanupTimer = new Timer(
            _ => CleanupStaleEntries(),
            state: null,
            dueTime: CleanupInterval,
            period: CleanupInterval);
    }

    public void AddPath(string path)
    {
        if (!Directory.Exists(path) || !_isRunning) return;
        AddWatcher(path);
    }

    private void AddWatcher(string path)
    {
        var watcher = new FileSystemWatcher(path)
        {
            NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName,
            IncludeSubdirectories = true,
            EnableRaisingEvents   = true
        };

        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;

        _watchers.Add(watcher);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!_isRunning) return;

        var ext = Path.GetExtension(e.FullPath);
        if (!WatchedExtensions.Contains(ext)) return;

        var fileName = Path.GetFileName(e.FullPath);
        if (fileName.StartsWith(".") || fileName.EndsWith("~") ||
            fileName.EndsWith(".tmp") || fileName.Contains("___jb_"))
            return;

        var now = DateTime.UtcNow;

        lock (_lastLogged)
        {
            if (_lastLogged.TryGetValue(e.FullPath, out var last) &&
                now - last < _debounce)
                return;

            _lastLogged[e.FullPath] = now;
        }

        _ = _logService.LogEventAsync(
            _sessionId,
            EventTypes.FileSavedAuto,
            $"File saved: {fileName}",
            metadata: e.FullPath);
    }

    private void CleanupStaleEntries()
    {
        if (!_isRunning) return;

        var cutoff = DateTime.UtcNow - StaleEntryMaxAge;
        lock (_lastLogged)
        {
            var stale = _lastLogged
                .Where(kv => kv.Value < cutoff)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in stale)
                _lastLogged.Remove(key);
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        _cleanupTimer?.Dispose();
        _cleanupTimer = null;

        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();

        lock (_lastLogged)
            _lastLogged.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}