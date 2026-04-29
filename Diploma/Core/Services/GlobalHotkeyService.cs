using System.Windows.Forms;
using Diploma.Core.Interfaces;
using Gma.System.MouseKeyHook;

namespace Diploma.Core.Services;

public class GlobalHotkeyService : IGlobalHotkeyService
{
    private IKeyboardMouseEvents? _hook;
    private bool _isRunning;
    private bool _ctrlDown;
    private bool _shiftDown;
    
    public event EventHandler? StartStopRequested;
    public event EventHandler? MarkerRequested;

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _hook = Hook.GlobalEvents();
            _hook.KeyDown += OnKeyDown;
            _hook.KeyUp += OnKeyUp;
        });
    }
    
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey) _ctrlDown = true;
        if (e.KeyCode == Keys.LShiftKey || e.KeyCode == Keys.RShiftKey) _shiftDown = true;

        if (_ctrlDown && _shiftDown && e.KeyCode == Keys.R)
        {
            StartStopRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (_ctrlDown && _shiftDown && e.KeyCode == Keys.M)
        {
            MarkerRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey) _ctrlDown = false;
        if (e.KeyCode == Keys.LShiftKey || e.KeyCode == Keys.RShiftKey) _shiftDown = false;
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_hook is null) return;
            _hook.KeyDown -= OnKeyDown;
            _hook.KeyUp -= OnKeyUp;
            _hook.Dispose();
            _hook = null;
        });
    }
    
    public void Dispose()
    {
        Stop();
    }
}