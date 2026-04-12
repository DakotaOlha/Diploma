using Diploma.Core.Interfaces;
using NAudio.Wave;

namespace Diploma.Core.Services;

public class AudioCaptureService : IAudioCaptureService
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _outputPath;
    private volatile bool _isRecording;
    private readonly object _writeLock = new();
    
    public bool IsRecording => _isRecording;
    
    public string? SelectedDevice { get; set; }

    public IReadOnlyList<string> GetAvailableDevices()
    {
        var devices = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            devices.Add(caps.ProductName);
        }
        return devices;
    }

    private int GetDeviceIndex()
    {
        if (SelectedDevice is null) return 0;

        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            if (WaveInEvent.GetCapabilities(i).ProductName == SelectedDevice)
                return i;
        }

        return 0;
    }

    public Task StartAsync(string outputPath, CancellationToken ct = default)
    {
        if (_isRecording) return Task.CompletedTask;

        _outputPath = outputPath;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = GetDeviceIndex(),
            WaveFormat = new WaveFormat(44100, 1)
        };
        
        _writer = new WaveFileWriter(_outputPath, _waveIn.WaveFormat);

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        
        _waveIn.StartRecording();
        _isRecording = true;
        
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!_isRecording) return Task.CompletedTask;
        _isRecording = false;
        _waveIn?.StopRecording();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _waveIn?.Dispose();
        _waveIn = null;

        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
    
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_writeLock)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_writeLock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }

        _waveIn?.Dispose();
        _waveIn = null;
    }
}