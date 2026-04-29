using Diploma.Core.Interfaces;
using Gma.System.MouseKeyHook;
using System.Windows.Forms;
using Diploma.Core.Models;

namespace Diploma.Core.Services;

public class InputMonitorService : IInputMonitorService
{
    private const int IdleThresholdMinutes = 5;
    
    private ModeProfile _currentProfile;
    private readonly ModeProfileService _profileService;
    
    private readonly ILogService _logService;
    private readonly InputProcessorService _processor;
    
    private readonly WindowTitleMonitor _windowTitleMonitor;
    private readonly ProcessMonitor _processMonitor;
    private readonly FileSystemMonitor _fileSystemMonitor;

    private IKeyboardMouseEvents? _hook;
    private System.Threading.Timer? _idleTimer;
    private DateTime _lastActivityTime;
    private int _sessionId;
    private bool _idleLogged;
    private bool _ctrlDown;
    private bool _shiftDown;
    private bool _isRunning;
    
    public InputMonitorService(ILogService logService, ModeProfileService profileService)
    {
        _logService     = logService;
        _profileService = profileService;
        _processor      = new InputProcessorService(logService, profileService);
        _currentProfile = profileService.GetProfile(RecordingMode.Personal);
        
        _windowTitleMonitor = new WindowTitleMonitor(logService);
        _processMonitor     = new ProcessMonitor(logService);
        _fileSystemMonitor  = new FileSystemMonitor(logService);
    }

    public bool IsRunning => _isRunning;

    public void Start(int sessionId)
    {
        if (_isRunning) return;
        _sessionId = sessionId;
        _isRunning = true;
        _lastActivityTime = DateTime.UtcNow;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _hook = Hook.GlobalEvents();
            _hook.KeyDown += OnKeyDown;
            _hook.KeyUp += OnKeyUp;
        });

        _idleTimer = new System.Threading.Timer(CheckIdle, null, 10000, 30000);
        
        _windowTitleMonitor.Start(sessionId);
        _processMonitor.Start(sessionId);
    }
    
    public void SetMode(RecordingMode mode)
    {
        _currentProfile = _profileService.GetProfile(mode);
        _processor.SetMode(mode);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        UpdateActivity();

        if (e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey) _ctrlDown = true;
        if (e.KeyCode == Keys.LShiftKey || e.KeyCode == Keys.RShiftKey) _shiftDown = true;

        _ = _processor.ProcessShortcutAsync(_sessionId, e.KeyCode, _ctrlDown, _shiftDown);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey) _ctrlDown = false;
        if (e.KeyCode == Keys.LShiftKey || e.KeyCode == Keys.RShiftKey) _shiftDown = false;
    }

    private void UpdateActivity()
    {
        _lastActivityTime = DateTime.UtcNow;
        if (_idleLogged)
        {
            _idleLogged = false;
            if (_currentProfile.IsAllowed(EventTypes.IdleEnd))
                _ = _logService.LogEventAsync(_sessionId, EventTypes.IdleEnd, "User returned");
        }
    }

    private void CheckIdle(object? state)
    {
        if (!_isRunning || _idleLogged) return;
        if (!_currentProfile.IsAllowed(EventTypes.IdleStart)) return;

        if ((DateTime.UtcNow - _lastActivityTime).TotalMinutes >= IdleThresholdMinutes)
        {
            _idleLogged = true;
            _ = _logService.LogEventAsync(
                _sessionId, EventTypes.IdleStart, $"Idle for {IdleThresholdMinutes}m");
        }
    }
    
    public void AddWatchPath(string path)
    {
        _fileSystemMonitor.AddPath(path);
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        _idleTimer?.Dispose();
        _idleTimer = null;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_hook is null) return;
            _hook.KeyDown -= OnKeyDown;
            _hook.KeyUp -= OnKeyUp;
            _hook.Dispose();
            _hook = null;
        });
        
        _windowTitleMonitor.Stop();
        _processMonitor.Stop();
        _fileSystemMonitor.Stop();
    }

    public void Dispose()
    {
        Stop();
        _windowTitleMonitor.Dispose();
        _processMonitor.Dispose();
        _fileSystemMonitor.Dispose();
    }
}