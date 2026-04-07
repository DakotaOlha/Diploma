using System.Runtime.InteropServices;
using System.Text;
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
    private const int FFmpegShutdownTimeoutMs = 15_000;
    private const int FirstFrameTimeoutSeconds = 5;
    private const int FramePoolBufferCount = 2;
    private const int TargetFrameRate = 30;
    
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private volatile bool _isRecording;
    private volatile byte[]? _latestFrame;
    private bool _disposed;
    
    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    
    private readonly object _logLock = new();
    private readonly StringBuilder _logBuffer = new();
    
    public bool IsRecording => _isRecording;
    
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? RecordingStarted;

    public Task StartAsync(string outputPath, CancellationToken ct = default)
    {
        if (_isRecording) return Task.CompletedTask;
        
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRecording = true;
        
        _captureTask = Task.Run(() => CaptureLoop(outputPath, _cts.Token), _cts.Token);
        return Task.CompletedTask;
    }
    
    public async Task StopAsync()
    {
        _cts?.Cancel();
        _isRecording = false;
        
        if (_captureTask != null)
            await _captureTask.ConfigureAwait(false);
        
        Log("Recording stopped");
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _cts?.Cancel();
        _captureTask?.Wait(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
        _d3dContext?.Dispose();
        _d3dDevice?.Dispose();
    }
    
    private async Task CaptureLoop(string outputPath, CancellationToken token)
    {
        try
        {
            var item = await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                var hwnd = new WindowInteropHelper(Application.Current.MainWindow).Handle;
                return await CapturePickerHelper.PickAsync(hwnd);
            }).Task.Unwrap();

            if (item == null)
            {
                _isRecording = false;
                return;
            }

            Log("Recording started");
            
            var winrtDevice = CreateDevice();

            Direct3D11CaptureFramePool? framePool = null;
            GraphicsCaptureSession? session = null;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    winrtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    FramePoolBufferCount,
                    item.Size);

                session = framePool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = true;

                framePool.FrameArrived += (pool, _) =>
                {
                    using var frame = pool.TryGetNextFrame();
                    if (frame == null) return;

                    var bytes = ConvertFrameToBytes(frame);

                    if (bytes != null)
                        _latestFrame = bytes;
                };

                session.StartCapture();
            });

            var firstFrameTimeout = DateTime.Now.AddSeconds(FirstFrameTimeoutSeconds);
            while (_latestFrame == null && DateTime.Now < firstFrameTimeout && !token.IsCancellationRequested)
            {
                await Task.Delay(50, token);
            }

            if (_latestFrame == null)
            {
                Log("No frames received, aborting");
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    session?.Dispose();
                    framePool?.Dispose();
                });
                return;
            }

            int width = item.Size.Width;
            int height = item.Size.Height;

            await EncodeThroughFFmpeg(outputPath, width, height, token);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                session?.Dispose();
                framePool?.Dispose();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"Capture error: {ex.GetType().Name}: {ex.Message}");
        }
    }
    
    private async Task EncodeThroughFFmpeg(
        string outputPath,
        int width, int height,
        CancellationToken token)
    {
        bool stopRequested = false;
        bool firstFrameFired = false;
        
        IEnumerable<IVideoFrame> GenerateFrames()
        {
            byte[]? lastFrame = null;
            
            while (lastFrame == null && !stopRequested)
            {
                lastFrame = _latestFrame;
                if (lastFrame == null)
                    Thread.Sleep(10);
            }
            
            if (lastFrame == null) yield break;
            
            if (!firstFrameFired)
            {
                firstFrameFired = true;
                RecordingStarted?.Invoke(this, EventArgs.Empty);
            }
            
            var startTime = DateTime.UtcNow;
            long frameIndex = 0;
            
            while (!stopRequested)
            {
                var current = _latestFrame;
                if (current != null)
                    lastFrame = current;

                yield return new BgraVideoFrame(lastFrame, width, height);

                frameIndex++;

                var nextFrameTime = startTime + TimeSpan.FromSeconds(frameIndex / (double)TargetFrameRate);
                var sleepTime = nextFrameTime - DateTime.UtcNow;

                if (sleepTime > TimeSpan.Zero)
                    Thread.Sleep(sleepTime);
            }
        }
        
        var videoFramesSource = new RawVideoPipeSource(GenerateFrames())
        {
            FrameRate = 30
        };
        
        using var emergencyCts = new CancellationTokenSource();

        try 
        {
            var analyzeTask = FFMpegArguments
                .FromPipeInput(videoFramesSource, opts => opts
                    .WithVideoCodec("rawvideo")
                    .ForceFormat("rawvideo")
                    .WithCustomArgument($"-pix_fmt bgra -s {width}x{height}"))
                .OutputToFile(outputPath, overwrite: true, opts => opts
                    .WithVideoCodec("libx264")
                    .WithConstantRateFactor(23)
                    .WithCustomArgument("-preset ultrafast")
                    .WithCustomArgument("-pix_fmt yuv420p"))
                .CancellableThrough(emergencyCts.Token)
                .ProcessAsynchronously();
            
            await Task.Delay(-1, token).ContinueWith(_ => { });
        
            stopRequested = true; 
            Log("Finishing writing frames...");
        
            var completed = await Task.WhenAny(analyzeTask, Task.Delay(FFmpegShutdownTimeoutMs));
            if (completed != analyzeTask)
            {
                Log("FFmpeg timeout — force cancelling");
                emergencyCts.Cancel();
                await analyzeTask.ContinueWith(_ => { });
            }
            else
            {
                await analyzeTask;
                Log("Recording saved successfully");
            }
        }
        catch (Exception ex)
        {
            Log($"Encoding error: {ex.GetType().Name}: {ex.Message}");
        }
    }
    
    private IDirect3DDevice CreateDevice()
    {
        var result = D3D11.D3D11CreateDevice(
            (IntPtr)null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null!,
            out var device,
            out var context);
        
        result.CheckError();

        _d3dDevice = device;
        _d3dContext = context;

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();

        CreateDirect3D11DeviceFromDXGIDevice(
            dxgiDevice.NativePointer,
            out var pDevice);

        var winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(pDevice);
        Marshal.Release(pDevice);

        return winrtDevice;
    }

    private byte[]? ConvertFrameToBytes(Direct3D11CaptureFrame frame)
    {
        if (_d3dDevice == null || _d3dContext == null) return null;

        try
        {
            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();

            Guid guid = typeof(ID3D11Texture2D).GUID;
            IntPtr texturePtr = access.GetInterface(guid);
            
            if (texturePtr == IntPtr.Zero)
            {
                return null;
            }

            using var texture = new ID3D11Texture2D(texturePtr);

            var desc = texture.Description;
            
            var stagingDesc = new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read
            };

            using var stagingTexture = _d3dDevice.CreateTexture2D(stagingDesc);

            _d3dContext.CopyResource(stagingTexture, texture);

            var mapped = _d3dContext.Map(stagingTexture, 0, MapMode.Read, MapFlags.None);

            try
            {
                uint width = desc.Width;
                uint height = desc.Height;

                byte[] data = new byte[width * height * 4];

                unsafe
                {
                    byte* srcPtr = (byte*)mapped.DataPointer;
                    uint rowPitch = mapped.RowPitch;

                    for (int y = 0; y < height; y++)
                    {
                        var sourceRow = new ReadOnlySpan<byte>(srcPtr + y * rowPitch, (int)(width * 4));
                        var destRow = new Span<byte>(data, (int)(y * width * 4), (int)(width * 4));
                        sourceRow.CopyTo(destRow);
                    }
                }
                return data;
            }
            finally
            {
                _d3dContext.Unmap(stagingTexture, 0);
            }
        }
        catch (Exception ex)
        {
            Log($"Capture error: {ex.GetType().Name}: {ex.Message}");
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