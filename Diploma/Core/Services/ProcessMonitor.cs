using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using System.Management;

namespace Diploma.Core.Services;

public class ProcessMonitor : IDisposable
{
    private static readonly Dictionary<string, string> IdeDisplayNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ["rider"]    = "JetBrains Rider",
        ["devenv"]   = "Visual Studio",
        ["code"]     = "Visual Studio Code",
        ["idea"]     = "IntelliJ IDEA",
        ["clion"]    = "CLion",
        ["pycharm"]  = "PyCharm",
        ["webstorm"] = "WebStorm",
        ["fleet"]    = "Fleet",
    };

    private readonly ILogService _logService;

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;

    private readonly Dictionary<int, string> _runningIdePids = new();

    private int  _sessionId;
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

        _runningIdePids.Clear();

        SnapshotRunningIdes();

        StartWmiWatchers();
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        DisposeWatchers();
        _runningIdePids.Clear();
    }
    
    private void SnapshotRunningIdes()
    {
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (IdeDisplayNames.ContainsKey(name))
                        _runningIdePids[process.Id] = name;
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch { }
    }

    private void StartWmiWatchers()
    {
        try
        {
            const string startQuery =
                "SELECT * FROM Win32_ProcessStartTrace WITHIN 1";
            const string stopQuery =
                "SELECT * FROM Win32_ProcessStopTrace WITHIN 1";

            _startWatcher = new ManagementEventWatcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new EventQuery(startQuery));

            _stopWatcher = new ManagementEventWatcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new EventQuery(stopQuery));

            _startWatcher.EventArrived += OnProcessStarted;
            _stopWatcher.EventArrived  += OnProcessStopped;

            _startWatcher.Start();
            _stopWatcher.Start();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex,
                "ProcessMonitor: WMI watcher setup failed — IDE tracking disabled");
            DisposeWatchers();
        }
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        if (!_isRunning) return;

        try
        {
            var pid  = Convert.ToInt32(e.NewEvent["ProcessID"]);
            var name = e.NewEvent["ProcessName"]?.ToString() ?? string.Empty;

            name = System.IO.Path.GetFileNameWithoutExtension(name);

            if (!IdeDisplayNames.TryGetValue(name, out var displayName)) return;

            lock (_runningIdePids)
            {
                if (_runningIdePids.ContainsKey(pid)) return;
                _runningIdePids[pid] = name;
            }

            _ = _logService.LogEventAsync(
                _sessionId,
                EventTypes.IdeOpened,
                $"IDE opened: {displayName}",
                metadata: name);
        }
        catch { }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        if (!_isRunning) return;

        try
        {
            var pid = Convert.ToInt32(e.NewEvent["ProcessID"]);

            string? name;
            lock (_runningIdePids)
            {
                if (!_runningIdePids.TryGetValue(pid, out name)) return;
                _runningIdePids.Remove(pid);
            }

            var displayName = IdeDisplayNames.GetValueOrDefault(name, name);

            _ = _logService.LogEventAsync(
                _sessionId,
                EventTypes.IdeClosed,
                $"IDE closed: {displayName}",
                metadata: name);
        }
        catch { }
    }

    private void DisposeWatchers()
    {
        if (_startWatcher is not null)
        {
            _startWatcher.EventArrived -= OnProcessStarted;
            try { _startWatcher.Stop(); } catch { }
            _startWatcher.Dispose();
            _startWatcher = null;
        }

        if (_stopWatcher is not null)
        {
            _stopWatcher.EventArrived -= OnProcessStopped;
            try { _stopWatcher.Stop(); } catch { }
            _stopWatcher.Dispose();
            _stopWatcher = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}