using Diploma.Core.Interfaces;
using NAudio.Wave;

namespace Diploma.Core.Services;

public class AudioCaptureService : IAudioCaptureService
{
    private const int SampleRate = 44100;
    private const int Channels   = 1;
    
    private readonly object _stateLock = new();
    private readonly object _writeLock = new();
    
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private bool _isRecording;
    private bool _disposed;
    
    public bool IsRecording => _isRecording;
    public string? SelectedDevice { get; set; }
    
    public IReadOnlyList<string> GetAvailableDevices()
    {
        return Enumerable.Range(0, WaveInEvent.DeviceCount)
            .Select(i => WaveInEvent.GetCapabilities(i).ProductName)
            .ToList();
    }

    public Task StartAsync(string outputPath, CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_isRecording) return Task.CompletedTask;
            _isRecording = true;
        }

        try
        {
            int deviceIndex = GetDeviceIndex();

            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat = new WaveFormat(SampleRate, Channels)
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            _writer = new WaveFileWriter(outputPath, _waveIn.WaveFormat);

            _waveIn.StartRecording();
        }
        catch (Exception)
        {
            lock (_stateLock) _isRecording = false;
            Cleanup();
            throw;
        }
        
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_isRecording)
        {
            _waveIn?.StopRecording();
            _isRecording = false;
        }

        return Task.CompletedTask;
    }
    
    private int GetDeviceIndex() =>
        SelectedDevice is null ? 0 :
            Enumerable.Range(0, WaveInEvent.DeviceCount)
                .FirstOrDefault(i => WaveInEvent.GetCapabilities(i).ProductName == SelectedDevice);
    
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_writeLock)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) => Cleanup();
    
    
    private void Cleanup()
    {
        lock (_stateLock)
        {
            _waveIn?.Dispose();
            _waveIn = null;
        }
        
        lock (_writeLock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        Cleanup();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}