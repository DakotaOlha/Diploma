using Diploma.Core.Interfaces;
using Diploma.Core.Models;

namespace Diploma.Core.Services;

public class ProcessMonitor : IDisposable
{
    private static readonly Dictionary<string, string> IdeDisplayNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ["rider"]   = "JetBrains Rider",
        ["devenv"]  = "Visual Studio",
        ["code"]    = "Visual Studio Code",
        ["idea"]    = "IntelliJ IDEA",
        ["clion"]   = "CLion",
        ["pycharm"] = "PyCharm",
        ["webstorm"]= "WebStorm",
        ["fleet"]   = "Fleet",
    };
    
    private readonly ILogService _logService;
    private Timer? _timer;

    private readonly HashSet<string> _runningIdes = new();
    private int _sessionId;
    private bool _isRunning;
    private bool _disposed;

    public ProcessMonitor(ILogService logService)
    {
        _logService = logService;
    }

    public void Start(int sessionId)
    {
        if (_disposed || _isRunning) return;
        
        _sessionId = sessionId;
        _isRunning = true;
        
        _runningIdes.Clear();
        foreach (var ide in GetRunningIdes())
            _runningIdes.Add(ide);
        
        _timer?.Dispose();
        _timer = new Timer(Tick, null, 4000, 4000);
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _timer?.Dispose();
        _timer = null;
    }

    private void Tick(object state)
    {
        if (!_isRunning) return;

        try
        {
            var current = GetRunningIdes();

            foreach (var ide in current.Except(_runningIdes))
            {
                var displayName = IdeDisplayNames.GetValueOrDefault(ide, ide);
                _ = _logService.LogEventAsync(
                    _sessionId,
                    EventTypes.IdeOpened,
                    $"IDE opened: {displayName}",
                    metadata: ide);

                _runningIdes.Add(ide);
            }
            
            foreach (var ide in _runningIdes.Except(current).ToList())
            {
                var displayName = IdeDisplayNames.GetValueOrDefault(ide, ide);
                _ = _logService.LogEventAsync(
                    _sessionId,
                    EventTypes.IdeClosed,
                    $"IDE closed: {displayName}",
                    metadata: ide);

                _runningIdes.Remove(ide);
            }
        }
        catch { }
    }
    
    private static HashSet<string> GetRunningIdes()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                if (IdeDisplayNames.ContainsKey(process.ProcessName))
                    result.Add(process.ProcessName);
            }
        }
        catch { }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}