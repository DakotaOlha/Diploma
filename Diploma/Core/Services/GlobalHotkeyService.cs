using System.Windows.Forms;
using Diploma.Core.Interfaces;
using Gma.System.MouseKeyHook;

namespace Diploma.Core.Services;

public class GlobalHotkeyService : IGlobalHotkeyService
{
    private IKeyboardMouseEvents? _hook;
    private bool _isRunning;
    
    public event EventHandler? StartStopRequested;
    public event EventHandler? MarkerRequested;
    public event EventHandler? ScreenshotRequested;
    public event EventHandler? WhiteboardRequested;

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _hook = Hook.GlobalEvents();
            _hook.KeyDown += OnKeyDown;
        });
    }
    
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.Shift && e.KeyCode == Keys.R)
        {
            StartStopRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Control && e.Shift && e.KeyCode == Keys.M)
        {
            MarkerRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Control && e.Shift && e.KeyCode == Keys.S)
        {
            ScreenshotRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Control && e.Shift && e.KeyCode == Keys.W)
        {
            WhiteboardRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_hook is null) return;
            _hook.KeyDown -= OnKeyDown;
            _hook.Dispose();
            _hook = null;
        });
    }
    
    public void Dispose()
    {
        Stop();
    }
}