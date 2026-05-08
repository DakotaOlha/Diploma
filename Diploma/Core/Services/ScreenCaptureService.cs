using System.Buffers;
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

public class ScreenCaptureService: IScreenCaptureService, IDisposable
{
    private const int TargetFrameRate = 30;
    private const int ChannelCapacity = 8;
    private const int FirstFrameTimeoutMs   = 5_000;
    private const long SnapshotIntervalMs = 200;
    
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private Task? _encodeTask;
    
    private Channel<BgraVideoFrame>? _frameChannel;
    private volatile bool _isRecording;
    private bool _disposed;
    
    private readonly object _snapshotLock = new();
    private byte[]? _latestFrame;
    private int _latestFrameWidth;
    private int _latestFrameHeight;
    private long _lastSnapshotTickMs;
    
    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    
    private ID3D11Texture2D? _stagingTexture;
    private (int W, int H) _stagingSize;
    private readonly object _stagingLock = new();
    
    private readonly object _logLock = new();
    private readonly StringBuilder _logBuffer = new();
    
    public bool IsRecording => _isRecording;
    
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? RecordingStarted;
    public event EventHandler? CaptureTargetSelected;
    
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);
 
    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);
    
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public async Task<bool> StartAsync(string outputPath, CancellationToken ct = default)
    {
        if (_isRecording) return false;

        var item = await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var hwnd = new WindowInteropHelper(Application.Current.MainWindow).Handle;
            return await CapturePickerHelper.PickAsync(hwnd);
        }).Task.Unwrap();

        if (item == null) return false;

        _frameChannel = Channel.CreateBounded<BgraVideoFrame>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true,
        });
    
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRecording = true;

        CaptureTargetSelected?.Invoke(this, EventArgs.Empty);
    
        _captureTask = Task.Run(() => CaptureLoopAsync(item, outputPath, _cts.Token));
    
        return true;
    }
    
    public async Task StopAsync()
    {
        if (!_isRecording) return;
 
        _cts?.Cancel();
        _isRecording = false;
 
        if (_captureTask != null)
        {
            try   { await _captureTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
 
        if (_encodeTask != null)
        {
            try   { await _encodeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
 
        ResetD3DState();
 
        Log("Recording stopped");
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

        if (snapshot is null || width == 0 || height == 0)
            return null;

        var fileName   = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
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

    private static void SaveBgraToPng(byte[] bgraData, int width, int height, string path)
    {
        var bitmapSource = BitmapSource.Create(
            width, height,
            96.0, 96.0,
            PixelFormats.Bgra32,
            null,
            bgraData,
            width * 4);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

        using var stream = File.Create(path);
        encoder.Save(stream);
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
    
    private async Task CaptureLoopAsync(GraphicsCaptureItem item, string outputPath, CancellationToken token)
    {
        ChannelWriter<BgraVideoFrame> writer = _frameChannel!.Writer;
        
        try
        {
            Log("Capture target selected, initialising D3D…");
            
            var winrtDevice = CreateD3DDevice();
            int width  = item.Size.Width;
            int height = item.Size.Height;
                
            Direct3D11CaptureFramePool? framePool = null;
            GraphicsCaptureSession?     session   = null;
 
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
                    using var frame = pool.TryGetNextFrame();
                    if (frame is null) return;
 
                    var bytes = ConvertFrameToBytes(frame, width, height);
                    if (bytes is null) return;
 
                    var videoFrame = new BgraVideoFrame(bytes, width, height, pooled: true);
                    writer.TryWrite(videoFrame);
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
            catch (OperationCanceledException) {  }
 
            if (!gotFirstFrame)
            {
                Log("No frames received within timeout — aborting.");
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    session?.Dispose();
                    framePool?.Dispose();
                });
                writer.Complete();
                return;
            }
 
            Log("First frame received — starting encode…");
            RecordingStarted?.Invoke(this, EventArgs.Empty);
 
            _encodeTask = Task.Run(() => EncodeLoopAsync(outputPath, width, height, token));
 
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
 
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                session?.Dispose();
                framePool?.Dispose();
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Capture error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            writer.TryComplete();
        }
    }
    
    private async Task EncodeLoopAsync(string outputPath, int width, int height, CancellationToken token)
    {
        IEnumerable<IVideoFrame> FrameSource(CancellationToken ct)
        {
            var reader          = _frameChannel!.Reader;
            BgraVideoFrame? lastFrame   = null;
            BgraVideoFrame? prevYielded = null;
            
            var frameSw      = System.Diagnostics.Stopwatch.StartNew();
            long frameBudget = (long)(1000.0 / TargetFrameRate); 
            
            while (!ct.IsCancellationRequested && !reader.TryRead(out lastFrame))
                Thread.SpinWait(100);

            if (ct.IsCancellationRequested || lastFrame is null)
                yield break;

            timeBeginPeriod(1);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    prevYielded?.Return();
                    prevYielded = null;

                    while (reader.TryRead(out var newFrame))
                    {
                        if (!ReferenceEquals(lastFrame, newFrame))
                            lastFrame.Return();
                        lastFrame = newFrame;
                    }

                    if (lastFrame == null)
                        break;

                    var frameToYield = lastFrame;
                    prevYielded = frameToYield;
                    yield return frameToYield;

                    long remaining = frameBudget - frameSw.ElapsedMilliseconds;
                    if (remaining > 0)
                    {
                        const int SliceMs = 4;
                        while (remaining > 0 && !ct.IsCancellationRequested)
                        {
                            if (reader.TryPeek(out _)) break;
                            int slice = (int)Math.Min(remaining, SliceMs);
                            Thread.Sleep(slice);
                            remaining -= slice;
                        }
                    }

                    frameSw.Restart();
                }
            }
            finally
            {
                timeEndPeriod(1);
                prevYielded?.Return();
            }
        }
 
        var videoSource = new RawVideoPipeSource(FrameSource(token)) { FrameRate = TargetFrameRate };
 
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

                    if (videoEncoder == "libx264")
                    {
                        opts.WithConstantRateFactor(23)
                            .WithCustomArgument("-preset ultrafast");
                    }
                    else if (videoEncoder == "h264_nvenc")
                    {
                        opts.WithConstantRateFactor(23)
                            .WithCustomArgument("-preset p1")
                            .WithCustomArgument("-rc vbr")
                            .WithCustomArgument("-b:v 0");
                    }
                    else if (videoEncoder == "h264_amf")
                    {
                        opts.WithCustomArgument("-quality speed")
                            .WithCustomArgument("-rc cqp -qp_i 23 -qp_p 23");
                    }
                    else if (videoEncoder == "h264_qsv")
                    {
                        opts.WithCustomArgument("-preset veryfast")
                            .WithCustomArgument("-global_quality 23");
                    }
                })
                .CancellableThrough(token)
                .ProcessAsynchronously();
 
            var success = await encodeTask;
        
            if (success)
                Log("Encode finished — MP4 fully written.");
            else
                Log("Encode finished with errors or was force cancelled.");
        }
        catch (OperationCanceledException)
        {
            Log("Encode cancelled — normal shutdown.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Encode error: {ex.GetType().Name}: {ex.Message}");
        }
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

                    var stagingDesc = new Texture2DDescription
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
                    };

                    _stagingTexture = _d3dDevice.CreateTexture2D(stagingDesc);
                    _stagingSize    = ((int W, int H))(desc.Width, desc.Height);

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
    
    private void Log(string message)
    {
        lock (_logLock)
        {
            _logBuffer.AppendLine($"{DateTime.Now:HH:mm:ss.fff} | {message}");
            StatusChanged?.Invoke(this, _logBuffer.ToString());
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
 
        _cts?.Cancel();
 
        _captureTask?.Wait(TimeSpan.FromSeconds(2));
        _encodeTask?.Wait(TimeSpan.FromSeconds(5));
 
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