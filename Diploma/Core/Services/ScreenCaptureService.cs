using System.Buffers;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using Diploma.Helpers;
using FFMpegCore;
using FFMpegCore.Pipes;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinRT;

namespace Diploma.Core.Services;

internal enum CaptureState
{
    Idle      = 0,
    Prepared  = 1,
    Recording = 2,
    Stopping  = 3,
}

public class ScreenCaptureService : IScreenCaptureService, IDisposable
{
    private const int HighFrameRate = 30;
    private const int LowFrameRate  = 15;

    private const double DropRateHighThreshold = 0.10;
    private const double DropRateLowThreshold  = 0.02;
    private const int    AdaptiveWindowSeconds  = 3;

    private const int  ChannelCapacity     = 8;
    private const int  FirstFrameTimeoutMs = 5_000;
    private const long SnapshotIntervalMs  = 200;
    private const int  LogRingCapacity     = 50;
    
    private int _state = (int)CaptureState.Idle;
    
    private CaptureState State
    {
        get => (CaptureState)Volatile.Read(ref _state);
    }
    
    private bool TryTransition(CaptureState expected, CaptureState next)
        => Interlocked.CompareExchange(ref _state, (int)next, (int)expected)
           == (int)expected;
    
    private string?                  _outputPath;
    private GraphicsCaptureItem?     _captureItem;
    private IDirect3DDevice?         _winrtDevice;
    private Channel<BgraVideoFrame>? _frameChannel;
    
    private CancellationTokenSource? _cts;
    private Task?                    _captureTask;
    private Task?                    _encodeTask;
    private TaskCompletionSource<bool>? _stopRequested;
    
    private bool _disposed;

    private readonly object _snapshotLock   = new();
    private byte[]? _latestFrame;
    private int     _latestFrameWidth;
    private int     _latestFrameHeight;
    private long    _lastSnapshotTickMs;

    private ID3D11Device?        _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private ID3D11Texture2D?     _stagingTexture;
    private (int W, int H)       _stagingSize;
    private readonly object      _stagingLock = new();

    private long _droppedFramesTotal;
    private long _droppedFramesWindow;
    private long _deliveredFramesWindow;
    private volatile int _targetFrameRate = HighFrameRate;

    private readonly object   _logLock  = new();
    private readonly string[] _logRing  = new string[LogRingCapacity];
    private int               _logHead;
    private int               _logCount;

    public bool IsRecording => State == CaptureState.Recording;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler?         RecordingStarted;
    public event EventHandler?         CaptureTargetSelected;
    public event EventHandler<DropStatsEventArgs>? DropStatsChanged;

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public async Task PrepareAsync(string outputPath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (!TryTransition(CaptureState.Idle, CaptureState.Prepared))
        {
            throw new InvalidOperationException(
                $"PrepareAsync can only be called from the Idle state (current: {State}).");
        }
        
        try
        {
            ct.ThrowIfCancellationRequested();
 
            Log("Opening capture-target picker…");
 
            var item = await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                var hwnd = new WindowInteropHelper(Application.Current.MainWindow).Handle;
                return await CapturePickerHelper.PickAsync(hwnd);
            }).Task.Unwrap().ConfigureAwait(false);
 
            if (item is null)
            {
                Log("Picker cancelled by user.");
                Interlocked.Exchange(ref _state, (int)CaptureState.Idle);
                throw new OperationCanceledException("Capture target picker was cancelled.");
            }
 
            ct.ThrowIfCancellationRequested();
 
            Log("Initialising D3D device…");
            var winrtDevice = CreateD3DDevice();
 
            var channel = Channel.CreateBounded<BgraVideoFrame>(
                new BoundedChannelOptions(ChannelCapacity)
                {
                    FullMode     = BoundedChannelFullMode.DropOldest,
                    SingleWriter = true,
                    SingleReader = true,
                });
 
            _outputPath   = outputPath;
            _captureItem  = item;
            _winrtDevice  = winrtDevice;
            _frameChannel = channel;
 
            _targetFrameRate       = HighFrameRate;
            _droppedFramesTotal    = 0;
            _droppedFramesWindow   = 0;
            _deliveredFramesWindow = 0;
 
            Log("Preparation complete — ready to begin capture.");
 
            CaptureTargetSelected?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            Interlocked.Exchange(ref _state, (int)CaptureState.Idle);
            ResetD3DState();
            _outputPath  = null;
            _captureItem = null;
            _winrtDevice = null;
            _frameChannel?.Writer.TryComplete();
            _frameChannel = null;
            throw;
        }
    }
    
    public async Task BeginCaptureAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
 
        if (!TryTransition(CaptureState.Prepared, CaptureState.Recording))
        {
            throw new InvalidOperationException(
                $"BeginCaptureAsync requires Prepared state (current: {State}).");
        }
 
        if (_captureItem is null || _frameChannel is null || _outputPath is null)
        {
            Interlocked.Exchange(ref _state, (int)CaptureState.Idle);
            throw new InvalidOperationException(
                "Capture context is missing. Call PrepareAsync first.");
        }
 
        ct.ThrowIfCancellationRequested();
 
        _stopRequested = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
 
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
 
        Log("Beginning capture…");
 
        _captureTask = Task.Run(
            () => CaptureLoopAsync(_captureItem, _outputPath, _winrtDevice!, _cts.Token),
            _cts.Token);
        
        var startupCheck = await Task.WhenAny(
                _captureTask,
                Task.Delay(100, _cts.Token))
            .ConfigureAwait(false);
 
        if (startupCheck == _captureTask)
        {
            try { await _captureTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"Capture startup failed: {ex.Message}");
                // Roll back to Idle.
                await StopAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    public async Task StopAsync()
    {
        var prev = (CaptureState)Interlocked.CompareExchange(
            ref _state,
            (int)CaptureState.Stopping,
            (int)CaptureState.Recording);
 
        if (prev == CaptureState.Idle || prev == CaptureState.Stopping)
            return;
 
        if (prev == CaptureState.Prepared)
        {
            Interlocked.Exchange(ref _state, (int)CaptureState.Stopping);
        }
 
        Log("Stop requested…");
 
        _stopRequested?.TrySetResult(true);
 
        if (_encodeTask is not null)
        {
            var finished = await Task.WhenAny(_encodeTask, Task.Delay(30_000))
                .ConfigureAwait(false);
 
            if (finished != _encodeTask)
            {
                Log("StopAsync: encode did not finish in 30 s — forcing cancel.");
                _cts?.Cancel();
            }
        }
        
        _cts?.Cancel();
 
        if (_captureTask is not null)
        {
            try   { await _captureTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { Log($"CaptureTask error on stop: {ex.Message}"); }
        }
 
        ResetD3DState();
        CleanupPrepareContext();
 
        _cts?.Dispose();
        _cts = null;
 
        Interlocked.Exchange(ref _state, (int)CaptureState.Idle);
        Log("Recording stopped — service returned to Idle.");
    }
    
    private void CleanupPrepareContext()
    {
        _outputPath  = null;
        _captureItem = null;
        _winrtDevice = null;
        _frameChannel?.Writer.TryComplete();
        _frameChannel = null;
        _stopRequested = null;
        _captureTask   = null;
        _encodeTask    = null;
    }

    private async Task CaptureLoopAsync(
        GraphicsCaptureItem item,
        string outputPath,
        IDirect3DDevice winrtDevice,
        CancellationToken token)
    {
        var writer = _frameChannel!.Writer;
 
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession?     session   = null;
 
        try
        {
            Log("Initialising WinRT capture session…");
 
            int width  = item.Size.Width;
            int height = item.Size.Height;
 
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    winrtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    item.Size);
 
                session = framePool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = true;
 
                framePool.FrameArrived += (pool, _) =>
                {
                    if (_stopRequested?.Task.IsCompleted == true) return;
 
                    using var frame = pool.TryGetNextFrame();
                    if (frame is null) return;
 
                    var bytes = ConvertFrameToBytes(frame, width, height);
                    if (bytes is null) return;
 
                    var videoFrame = new BgraVideoFrame(bytes, width, height, pooled: true);
 
                    if (!writer.TryWrite(videoFrame))
                    {
                        Interlocked.Increment(ref _droppedFramesTotal);
                        Interlocked.Increment(ref _droppedFramesWindow);
                        videoFrame.Return();
                    }
                };
 
                session.StartCapture();
            });
 
            using var firstFrameCts = new CancellationTokenSource(FirstFrameTimeoutMs);
            using var linked        = CancellationTokenSource.CreateLinkedTokenSource(
                                          token, firstFrameCts.Token);
 
            bool gotFirstFrame = false;
            try
            {
                await foreach (var _ in _frameChannel!.Reader.ReadAllAsync(linked.Token))
                {
                    gotFirstFrame = true;
                    break;
                }
            }
            catch (OperationCanceledException) { /* timeout or external cancel */ }
 
            if (!gotFirstFrame || token.IsCancellationRequested)
            {
                Log("No frames received within timeout — aborting.");
                return;
            }
 
            Log("First frame received — firing RecordingStarted, starting encode…");
            RecordingStarted?.Invoke(this, EventArgs.Empty);
 
            _encodeTask = Task.Run(() => EncodeLoopAsync(outputPath, width, height));
 
            try
            {
                await Task.WhenAny(
                    _stopRequested!.Task,
                    Task.Delay(Timeout.Infinite, token));
            }
            catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException)
        {
            Log("CaptureLoop cancelled.");
        }
        catch (Exception ex)
        {
            Log($"Capture error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                session?.Dispose();
                framePool?.Dispose();
            });
 
            writer.TryComplete();
        }
    }

    private async Task EncodeLoopAsync(string outputPath, int width, int height)
    {
        var windowStart      = Environment.TickCount64;
        int currentFrameRate = _targetFrameRate;
 
        IEnumerable<IVideoFrame> FrameSource()
        {
            var reader = _frameChannel!.Reader;
 
            BgraVideoFrame? lastFrame   = null;
            BgraVideoFrame? prevYielded = null;
 
            while (!reader.TryRead(out lastFrame))
            {
                if (reader.Completion.IsCompleted) yield break;
                Thread.SpinWait(100);
            }
 
            if (lastFrame is null) yield break;
 
            timeBeginPeriod(1);
            try
            {
                while (!reader.Completion.IsCompleted || reader.TryRead(out _))
                {
                    prevYielded?.Return();
                    prevYielded = null;
 
                    while (reader.TryRead(out var newFrame))
                    {
                        if (!ReferenceEquals(lastFrame, newFrame))
                            lastFrame.Return();
                        lastFrame = newFrame;
                    }
 
                    if (lastFrame is null) break;
 
                    var nowMs     = Environment.TickCount64;
                    var windowLen = nowMs - windowStart;
 
                    if (windowLen >= AdaptiveWindowSeconds * 1000L)
                    {
                        long dropped   = Interlocked.Exchange(ref _droppedFramesWindow, 0);
                        long delivered = Interlocked.Exchange(ref _deliveredFramesWindow, 0);
                        long total     = dropped + delivered;
                        double dropRate = total > 0 ? (double)dropped / total : 0.0;
 
                        int newRate = _targetFrameRate;
                        if (dropRate > DropRateHighThreshold && _targetFrameRate == HighFrameRate)
                        {
                            newRate = LowFrameRate;
                            Log($"Adaptive: drop rate {dropRate:P0} → lowering fps to {LowFrameRate}");
                        }
                        else if (dropRate < DropRateLowThreshold && _targetFrameRate == LowFrameRate)
                        {
                            newRate = HighFrameRate;
                            Log($"Adaptive: drop rate {dropRate:P0} → restoring fps to {HighFrameRate}");
                        }
 
                        _targetFrameRate = newRate;
                        currentFrameRate = newRate;
 
                        var totalDropped = Interlocked.Read(ref _droppedFramesTotal);
                        RaiseDropStats(totalDropped, dropRate, currentFrameRate);
 
                        windowStart = nowMs;
                    }
 
                    Interlocked.Increment(ref _deliveredFramesWindow);
 
                    prevYielded = lastFrame;
                    yield return lastFrame;
 
                    long frameBudgetMs = (long)(1000.0 / currentFrameRate);
                    long remaining     = frameBudgetMs;
                    const int SliceMs  = 4;
                    while (remaining > 0)
                    {
                        if (reader.Completion.IsCompleted) break;
                        if (reader.TryPeek(out _))         break;
                        int slice = (int)Math.Min(remaining, SliceMs);
                        Thread.Sleep(slice);
                        remaining -= slice;
                    }
                }
            }
            finally
            {
                timeEndPeriod(1);
                prevYielded?.Return();
            }
        }
 
        var videoSource = new RawVideoPipeSource(FrameSource())
        {
            FrameRate = HighFrameRate,
        };
 
        try
        {
            Log("FFmpeg encode started…");
 
            var videoEncoder = HardwareEncoderDetector.Detect();
 
            var encodeTask = FFMpegArguments
                .FromPipeInput(videoSource, opts => opts
                    .WithVideoCodec("rawvideo")
                    .ForceFormat("rawvideo")
                    .WithCustomArgument($"-pix_fmt bgra -s {width}x{height}"))
                .OutputToFile(outputPath, overwrite: true, opts =>
                {
                    opts.WithVideoCodec(videoEncoder)
                        .WithCustomArgument("-pix_fmt yuv420p");
 
                    switch (videoEncoder)
                    {
                        case "libx264":
                            opts.WithConstantRateFactor(23)
                                .WithCustomArgument("-preset ultrafast");
                            break;
                        case "h264_nvenc":
                            opts.WithConstantRateFactor(23)
                                .WithCustomArgument("-preset p1")
                                .WithCustomArgument("-rc vbr")
                                .WithCustomArgument("-b:v 0");
                            break;
                        case "h264_amf":
                            opts.WithCustomArgument("-quality speed")
                                .WithCustomArgument("-rc cqp -qp_i 23 -qp_p 23");
                            break;
                        case "h264_qsv":
                            opts.WithCustomArgument("-preset veryfast")
                                .WithCustomArgument("-global_quality 23");
                            break;
                    }
                })
                .ProcessAsynchronously();
 
            var success = await encodeTask;
 
            if (success) Log("Encode finished — MP4 fully written.");
            else         Log("Encode finished with errors.");
 
            var totalDropped = Interlocked.Read(ref _droppedFramesTotal);
            if (totalDropped > 0)
                Log($"Total frames dropped during session: {totalDropped}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Encode error: {ex.GetType().Name}: {ex.Message}");
        }
    }
    
    public async Task<string?> TakeScreenshotAsync(string outputDir)
    {
        byte[]? snapshot;
        int width, height;

        lock (_snapshotLock)
        {
            snapshot = _latestFrame;
            width    = _latestFrameWidth;
            height   = _latestFrameHeight;
        }

        if (snapshot is null || width == 0 || height == 0) return null;

        var fileName   = $"screenshot_{DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}.png";
        var outputPath = Path.Combine(outputDir, fileName);

        await Task.Run(() => SaveBgraToPng(snapshot, width, height, outputPath));
        return outputPath;
    }

    public BitmapSource? GetLatestFrameAsBitmap()
    {
        byte[]? snapshot;
        int width, height;

        lock (_snapshotLock)
        {
            snapshot = _latestFrame;
            width    = _latestFrameWidth;
            height   = _latestFrameHeight;
        }

        if (snapshot is null || width == 0 || height == 0) return null;

        return BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, snapshot, width * 4);
    }
    
    private IDirect3DDevice CreateD3DDevice()
    {
        D3D11.D3D11CreateDevice(
            (IntPtr)null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null!,
            out var device,
            out var context).CheckError();

        _d3dDevice  = device;
        _d3dContext = context;

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var pDevice);

        var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pDevice);
        Marshal.Release(pDevice);
        return winrtDevice;
    }

    private byte[]? ConvertFrameToBytes(Direct3D11CaptureFrame frame, int width, int height)
    {
        if (_d3dDevice is null || _d3dContext is null) return null;
 
        try
        {
            var access     = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            var texturePtr = access.GetInterface(typeof(ID3D11Texture2D).GUID);
            if (texturePtr == IntPtr.Zero) return null;
 
            using var texture = new ID3D11Texture2D(texturePtr);
            var desc = texture.Description;
 
            lock (_stagingLock)
            {
                if (_stagingTexture is null || _stagingSize != (desc.Width, desc.Height))
                {
                    _stagingTexture?.Dispose();
                    _stagingTexture = _d3dDevice.CreateTexture2D(new Texture2DDescription
                    {
                        Width             = desc.Width,
                        Height            = desc.Height,
                        MipLevels         = 1,
                        ArraySize         = 1,
                        Format            = desc.Format,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage             = ResourceUsage.Staging,
                        BindFlags         = BindFlags.None,
                        CPUAccessFlags    = CpuAccessFlags.Read,
                    });
                    _stagingSize = ((int)desc.Width, (int)desc.Height);
                    Log($"StagingTexture (re)created: {desc.Width}×{desc.Height}");
                }
 
                _d3dContext.CopyResource(_stagingTexture, texture);
 
                var mapped = _d3dContext.Map(_stagingTexture, 0, MapMode.Read, MapFlags.None);
                try
                {
                    var data = ArrayPool<byte>.Shared.Rent(width * height * 4);
                    unsafe
                    {
                        byte* src = (byte*)mapped.DataPointer;
                        for (int y = 0; y < height; y++)
                        {
                            new ReadOnlySpan<byte>(src + y * mapped.RowPitch, width * 4)
                                .CopyTo(new Span<byte>(data, y * width * 4, width * 4));
                        }
                    }
 
                    var nowMs = Environment.TickCount64;
                    if (nowMs - _lastSnapshotTickMs >= SnapshotIntervalMs)
                    {
                        var snapshot = new byte[width * height * 4];
                        Buffer.BlockCopy(data, 0, snapshot, 0, snapshot.Length);
 
                        lock (_snapshotLock)
                        {
                            _latestFrame       = snapshot;
                            _latestFrameWidth  = width;
                            _latestFrameHeight = height;
                        }
 
                        _lastSnapshotTickMs = nowMs;
                    }
 
                    return data;
                }
                finally
                {
                    _d3dContext.Unmap(_stagingTexture, 0);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Frame conversion error: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void ResetD3DState()
    {
        lock (_stagingLock)
        {
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _stagingSize    = default;
        }

        _d3dContext?.Dispose();
        _d3dContext = null;

        _d3dDevice?.Dispose();
        _d3dDevice = null;
    }

    private void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} | {message}";
 
        string snapshot;
        lock (_logLock)
        {
            _logRing[_logHead] = line;
            _logHead  = (_logHead + 1) % LogRingCapacity;
            _logCount = Math.Min(_logCount + 1, LogRingCapacity);
 
            int count = _logCount;
            int start = count < LogRingCapacity ? 0 : _logHead;
 
            var sb = new StringBuilder(count * 80);
            for (int i = 0; i < count; i++)
                sb.AppendLine(_logRing[(start + i) % LogRingCapacity]);
 
            snapshot = sb.ToString();
        }
 
        StatusChanged?.Invoke(this, snapshot);
    }

    private void RaiseDropStats(long totalDropped, double dropRate, int fps)
    {
        var args = new DropStatsEventArgs(totalDropped, dropRate, fps);
        Task.Run(() => DropStatsChanged?.Invoke(this, args));
    }

    private static void SaveBgraToPng(byte[] bgraData, int width, int height, string path)
    {
        var bitmapSource = BitmapSource.Create(
            width, height, 96.0, 96.0,
            PixelFormats.Bgra32, null, bgraData, width * 4);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopRequested?.TrySetResult(true);
        _cts?.Cancel();

        _captureTask?.Wait(TimeSpan.FromSeconds(2));
        _encodeTask?.Wait(TimeSpan.FromSeconds(30));
        _cts?.Dispose();

        ResetD3DState();
    }
}

[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface(in Guid iid);
}

public sealed class DropStatsEventArgs : EventArgs
{
    public long   TotalDropped { get; }
    public double DropRate     { get; }
    public int    CurrentFps   { get; }

    public DropStatsEventArgs(long totalDropped, double dropRate, int currentFps)
    {
        TotalDropped = totalDropped;
        DropRate     = dropRate;
        CurrentFps   = currentFps;
    }
}