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
using WinRT;

namespace Diploma.Core.Services;

public class ScreenCaptureService: IScreenCaptureService, IDisposable
{
    private const int TargetFrameRate = 30;
    private const int ChannelCapacity = 8;
    private const int FirstFrameTimeoutMs   = 5_000;
    private const int FFmpegShutdownTimeoutMs = 30_000;
    private const int FirstFrameTimeoutSeconds = 5;
    private const int FramePoolBufferCount = 2;
    
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private Task? _encodeTask;
    
    private Channel<BgraVideoFrame>? _frameChannel;
    private volatile bool _isRecording;
    private bool _disposed;
    
    private volatile byte[]? _latestFrame;
    
    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    
    private readonly object _logLock = new();
    private readonly StringBuilder _logBuffer = new();
    
    public bool IsRecording => _isRecording;
    
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? RecordingStarted;
    public event EventHandler? CaptureTargetSelected;

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
            await _captureTask.ConfigureAwait(false);
        
        if (_encodeTask != null)
            await _encodeTask.ConfigureAwait(false);
        
        Log("Recording stopped");
    }
    
    private async Task CaptureLoopAsync(GraphicsCaptureItem item, string outputPath, CancellationToken token)
    {
        ChannelWriter<BgraVideoFrame> writer = _frameChannel!.Writer;
        
        try
        {

            if (item == null)
            {
                _isRecording = false;
                writer.Complete();
                return;
            }
            
            CaptureTargetSelected?.Invoke(this, EventArgs.Empty);
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
 
                    var videoFrame = new BgraVideoFrame(bytes, width, height);
                    
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
 
            _encodeTask = Task.Run(() => EncodeLoopAsync(outputPath, width, height));
 
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
    
    private async Task EncodeLoopAsync(string outputPath, int width, int height)
    {
        IEnumerable<IVideoFrame> FrameSource()
        {
            var reader = _frameChannel!.Reader;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long frameIndex = 0;
            double frameDuration = 1000.0 / TargetFrameRate; // 33.333 мс для 30 FPS
            BgraVideoFrame? lastFrame = null;
 
            var waitTask = reader.WaitToReadAsync().AsTask();
            waitTask.Wait();
            
            while (true)
            {
                while (reader.TryRead(out var newFrame))
                {
                    lastFrame = newFrame;
                }

                if (lastFrame == null || (!_isRecording && reader.Completion.IsCompleted))
                {
                    break;
                }

                yield return lastFrame;
                frameIndex++;

                double targetMs = frameIndex * frameDuration;
                double currentMs = sw.Elapsed.TotalMilliseconds;
                double sleepMs = targetMs - currentMs;

                if (sleepMs > 0)
                {
                    Thread.Sleep((int)sleepMs);
                }
            }
        }
 
        var videoSource = new RawVideoPipeSource(FrameSource()) { FrameRate = TargetFrameRate };
 
        try
        {
            Log("FFmpeg encode started…");
 
            using var forceCts     = new CancellationTokenSource();
            
            _cts?.Token.Register(() => forceCts.CancelAfter(FFmpegShutdownTimeoutMs));
            
            var encodeTask = FFMpegArguments
                .FromPipeInput(videoSource, opts => opts
                    .WithVideoCodec("rawvideo")
                    .ForceFormat("rawvideo")
                    .WithCustomArgument($"-pix_fmt bgra -s {width}x{height}"))
                .OutputToFile(outputPath, overwrite: true, opts => opts
                    .WithVideoCodec("libx264")
                    .WithConstantRateFactor(23)
                    .WithCustomArgument("-preset ultrafast")
                    .WithCustomArgument("-pix_fmt yuv420p"))
                .CancellableThrough(forceCts.Token)
                .ProcessAsynchronously();
 
            var success = await encodeTask;
        
            if (success)
                Log("Encode finished — MP4 fully written.");
            else
                Log("Encode finished with errors or was force cancelled.");
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
            var desc          = texture.Description;
 
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
 
            using var staging = _d3dDevice.CreateTexture2D(stagingDesc);
            _d3dContext.CopyResource(staging, texture);
 
            var mapped = _d3dContext.Map(staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                var data = new byte[width * height * 4];
                unsafe
                {
                    byte* src = (byte*)mapped.DataPointer;
                    for (int y = 0; y < height; y++)
                    {
                        new ReadOnlySpan<byte>(src + y * mapped.RowPitch, width * 4)
                            .CopyTo(new Span<byte>(data, y * width * 4, width * 4));
                    }
                }
                return data;
            }
            finally
            {
                _d3dContext.Unmap(staging, 0);
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
        _d3dContext?.Dispose();
        _d3dDevice?.Dispose();
    }
    
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);
}

[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface(in Guid iid);
}