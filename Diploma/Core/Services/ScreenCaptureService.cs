using System.Runtime.InteropServices;
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
using System.Runtime.InteropServices;
using System.Windows.Interop;
using WinRT;

namespace Diploma.Core.Services;

public class ScreenCaptureService: IScreenCaptureService, IDisposable
{
    private CancellationTokenSource? _cts;
    private bool _isRecording;
    private Vortice.Direct3D11.ID3D11Device? _d3dDevice;
    private Vortice.Direct3D11.ID3D11DeviceContext? _d3dContext;
    
    public bool IsRecording => _isRecording;
    public event EventHandler<string>? StatusChanged;
    
    private int _frameCount = 0;
    
    private readonly object _logLock = new();
    private string _logBuffer = "";

    private void Log(string message)
    {
        lock (_logLock)
        {
            _logBuffer += $"{DateTime.Now:HH:mm:ss.fff} | {message}\n";
            StatusChanged?.Invoke(this, _logBuffer);
        }
    }
    
    public async Task StartAsync(string outputPath, CancellationToken ct = default)
    {
        if (_isRecording) return;
        
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRecording = true;
        
        _ = Task.Run(() => CaptureLoop(outputPath, _cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _isRecording = false;
        Log("Recording stopped");
        await Task.Delay(500);
    }

    private async Task CaptureLoop(string outputPath, CancellationToken token)
    {
        try
        {
            GraphicsCaptureItem? item;
            
            item = await Application.Current.Dispatcher.InvokeAsync(async () =>
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
            var frameQueue = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();

            Direct3D11CaptureFramePool? framePool = null;
            GraphicsCaptureSession? session = null;

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
                    if (frame == null)
                    {
                        return;
                    }

                    var bytes = ConvertFrameToBytes(frame);

                    if (bytes != null)
                        frameQueue.Enqueue(bytes);
                };

                session.StartCapture();
            });

            var firstFrameTimeout = DateTime.Now.AddSeconds(5);
            while (frameQueue.IsEmpty && DateTime.Now < firstFrameTimeout && !token.IsCancellationRequested)
            {
                await Task.Delay(50, token);
            }

            if (frameQueue.IsEmpty)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    session?.Dispose();
                    framePool?.Dispose();
                });
                return;
            }

            int width = item.Size.Width;
            int height = item.Size.Height;

            await EncodeThroughFFmpeg(outputPath, frameQueue, width, height, token);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                session?.Dispose();
                framePool?.Dispose();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.ToString());
        }
    }
    
    private IDirect3DDevice CreateDevice()
    {
        D3D11.D3D11CreateDevice(
            (IntPtr)null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out var device,
            out var context);

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

    private IDirect3DDevice CreateWinRTDevice()
    {
        using var dxgiDevice = _d3dDevice!.QueryInterface<IDXGIDevice>();
    
        CreateDirect3D11DeviceFromDXGIDevice(
            dxgiDevice.NativePointer, out var pDevice);

        var result = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(pDevice);
        Marshal.Release(pDevice);
    
        return result;
    }
    
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private async Task EncodeThroughFFmpeg(
        string outputPath,
        System.Collections.Concurrent.ConcurrentQueue<byte[]> frameQueue,
        int width, int height,
        CancellationToken token)
    {
        bool stopRequested = false;
        
        IEnumerable<IVideoFrame> GenerateFrames()
        {
            while (!stopRequested || !frameQueue.IsEmpty)
            {
                if (frameQueue.TryDequeue(out var bytes))
                {
                    yield return new BgraVideoFrame(bytes, width, height);
                }
                else
                {
                    if (stopRequested) break;
                    Thread.Sleep(1);
                }
            }
        }
        
        var videoFramesSource = new RawVideoPipeSource(GenerateFrames())
        {
            FrameRate = 30
        };

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
                .ProcessAsynchronously();
            
            await Task.Delay(-1, token).ContinueWith(_ => { });
        
            stopRequested = true; 
        
            Log("Finishing writing frames...");
            await analyzeTask;
        }
        catch (Exception ex)
        {
            Log($"Encoding error: {ex.Message}");
        }
    }

    private byte[]? ConvertFrameToBytes(Direct3D11CaptureFrame frame)
    {
        if (_d3dDevice == null || _d3dContext == null) return null;

        try
        {
            // Отримуємо доступ до DXGI інтерфейсу через WinRT surface
            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();

            Guid guid = typeof(ID3D11Texture2D).GUID;
            IntPtr texturePtr = access.GetInterface(guid);
            
            if (texturePtr == IntPtr.Zero)
            {
                return null;
            }

            using var texture = new ID3D11Texture2D(texturePtr);

            var desc = texture.Description;
            
            // Створюємо staging texture для читання з CPU
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

            // Копіюємо GPU → CPU texture
            _d3dContext.CopyResource(stagingTexture, texture);

            var mapped = _d3dContext.Map(stagingTexture, 0, MapMode.Read, MapFlags.None);

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

            _d3dContext.Unmap(stagingTexture, 0);

            return data;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConvertFrame Error]: {ex}");
            return null;
        }
    }
    
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(in Guid iid);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _d3dContext?.Dispose();
        _d3dDevice?.Dispose();
    }
}