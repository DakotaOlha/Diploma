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
    
    public async Task StartAsync(string outputPath, CancellationToken ct = default)
    {
        if (_isRecording) return;
        
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRecording = true;
        
        StatusChanged?.Invoke(this, "Recording started");
        _ = Task.Run(() => CaptureLoop(outputPath, _cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _isRecording = false;
        StatusChanged?.Invoke(this, "Recording stopped");
        await Task.Delay(500);
    }

    private async Task CaptureLoop(string outputPath, CancellationToken token)
    {
        try
        { 
            InitializeDirect3D();
            
            GraphicsCaptureItem? item = null;
            var tcs = new TaskCompletionSource<GraphicsCaptureItem?>();

            Application.Current.Dispatcher.Invoke(async () =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(
                    Application.Current.MainWindow).Handle;
                var result = await CapturePickerHelper.PickAsync(hwnd);
                tcs.SetResult(result);
            });
            
            item = await tcs.Task;

            StatusChanged?.Invoke(this, item == null
                ? "Step 3: Item is NULL!"
                : $"Step 3: Got item {item.DisplayName}");
            
            if (item == null) return;

            StatusChanged?.Invoke(this, "Step 4: Creating WinRT device...");
            var winrtDevice = CreateWinRTDevice();
            var frameQueue = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();

            Direct3D11CaptureFramePool? framePool = null;
            GraphicsCaptureSession? session = null;

            // framePool і session МАЮТЬ створюватись на UI потоці
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusChanged?.Invoke(this, "Step 5: Creating frame pool...");
                framePool = Direct3D11CaptureFramePool.Create(
                    winrtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    item.Size);

                StatusChanged?.Invoke(this, "Step 6: Creating session...");
                session = framePool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = true;

                framePool.FrameArrived += (pool, _) =>
                {
                    StatusChanged?.Invoke(this, "FrameArrived called!");

                    using var frame = pool.TryGetNextFrame();
                    if (frame == null)
                    {
                        StatusChanged?.Invoke(this, "TryGetNextFrame = null!");
                        return;
                    }

                    StatusChanged?.Invoke(this, $"Frame: {frame.ContentSize.Width}x{frame.ContentSize.Height}");

                    var bytes = ConvertFrameToBytes(frame);
                    StatusChanged?.Invoke(this, bytes == null ? "ConvertFrame = NULL!" : $"Bytes: {bytes.Length}");

                    if (bytes != null)
                        frameQueue.Enqueue(bytes);
                };

                StatusChanged?.Invoke(this, "Step 7: StartCapture...");
                session.StartCapture();
            });

            // Тепер чекаємо фреймів у фоновому потоці
            StatusChanged?.Invoke(this, "Step 7.5: Waiting for first frame...");
            var firstFrameTimeout = DateTime.Now.AddSeconds(5);
            while (frameQueue.IsEmpty && DateTime.Now < firstFrameTimeout && !token.IsCancellationRequested)
            {
                await Task.Delay(50, token);
            }

            if (frameQueue.IsEmpty)
            {
                StatusChanged?.Invoke(this, "Error: No frames received!");
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    session?.Dispose();
                    framePool?.Dispose();
                });
                return;
            }

            int width = item.Size.Width;
            int height = item.Size.Height;

            StatusChanged?.Invoke(this, "Step 8: Starting FFmpeg...");
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
            StatusChanged?.Invoke(this, $"Error at: {ex.GetType().Name}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine(ex.ToString());
        }
    }
    
    private void InitializeDirect3D()
    {
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out _d3dDevice,
            out _d3dContext
        );
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
        IEnumerable<IVideoFrame> GenerateFrames()
        {
            while (!token.IsCancellationRequested)
            {
                if (frameQueue.TryDequeue(out var bytes))
                {
                    yield return new BgraVideoFrame(bytes, width, height);
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        }
        
        var videoFramesSource = new RawVideoPipeSource(GenerateFrames())
        {
            FrameRate = 30
        };

        await FFMpegArguments
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
    }

    private byte[]? ConvertFrameToBytes(Direct3D11CaptureFrame frame)
    {
        if(_d3dDevice == null || _d3dContext == null) return null;

        try
        {
            // Правильний спосіб отримати ID3D11Texture2D з WinRT поверхні
            var surface = frame.Surface;
            
            // Конвертуємо через WinRT ABI
            IntPtr surfacePtr = ((IWinRTObject)surface).NativeObject.ThisPtr;
            
            Guid texGuid = typeof(ID3D11Texture2D).GUID;
            Marshal.QueryInterface(surfacePtr, ref texGuid, out var pTexture);
            
            if (pTexture == IntPtr.Zero)
            {
                StatusChanged?.Invoke(this, "pTexture is Zero!");
                return null;
            }

            var texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(pTexture);
            Marshal.Release(pTexture);

            var desc = texture.Description;
            uint width = desc.Width;
            uint height = desc.Height;

            var stagingDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read
            };

            using var stagingTexture = _d3dDevice.CreateTexture2D(stagingDesc);
            _d3dContext.CopyResource(stagingTexture, texture);

            var mapped = _d3dContext.Map(stagingTexture, 0, MapMode.Read, MapFlags.None);

            uint stride = mapped.RowPitch;
            byte[] data = new byte[width * height * 4];

            unsafe
            {
                byte* src = (byte*)mapped.DataPointer;
                for (int y = 0; y < height; y++)
                {
                    new Span<byte>(src + y * stride, (int)(width * 4))
                        .CopyTo(data.AsSpan((int)(y * width * 4), (int)(width * 4)));
                }
            }

            _d3dContext.Unmap(stagingTexture, 0);
            texture.Dispose();

            return data;
        }
        catch (Exception ex)
        {
            // Тепер бачимо реальну помилку замість мовчазного null
            StatusChanged?.Invoke(this, $"ConvertFrame error: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _d3dContext?.Dispose();
        _d3dDevice?.Dispose();
    }
}