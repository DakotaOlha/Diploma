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

namespace Diploma.Core.Services;

public class ScreenCaptureService: IScreenCaptureService, IDisposable
{
    private CancellationTokenSource? _cts;
    private bool _isRecording;
    private Vortice.Direct3D11.ID3D11Device? _d3dDevice;
    private Vortice.Direct3D11.ID3D11DeviceContext? _d3dContext;
    
    public bool IsRecording => _isRecording;
    public event EventHandler<string>? StatusChanged;
    
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
            await Application.Current.Dispatcher.InvokeAsync(async () => 
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(Application.Current.MainWindow).Handle;
                    item = await CapturePickerHelper.PickAsync(hwnd);
                } );
            
            if (item == null) return;
            
            var frameQueue = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();

            var winrtDevice = CreateWinRTDevice();
            
            using var framePool = Direct3D11CaptureFramePool.Create(
                winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);
           
            using var session = framePool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = true;
           
            framePool.FrameArrived += (pool, _) =>
            { 
                using var frame = pool.TryGetNextFrame();
                if (frame == null) return;
               
                var bytes = ConvertFrameToBytes(frame);
                if (bytes != null) frameQueue.Enqueue(bytes); 
            };
           
            session.StartCapture();
           
            int width = item.Size.Width;
            int height = item.Size.Height;
           
            await EncodeThroughFFmpeg(outputPath, frameQueue, width, height, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Error: {ex.Message}");
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
        var pUnknow = Marshal.GetIUnknownForObject(dxgiDevice);

        Guid guid = typeof(IDirect3DDevice).GUID;
        Marshal.QueryInterface(pUnknow, ref guid, out var pDevice);
        Marshal.Release(pUnknow);

        var result = Marshal.GetObjectForIUnknown(pDevice) as IDirect3DDevice;
        Marshal.Release(pDevice);

        return result;
    }

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
            var surface = frame.Surface;
            
            var pUnknown = Marshal.GetIUnknownForObject(surface);
             Guid texGuid = typeof(ID3D11Texture2D).GUID;
             Marshal.QueryInterface(pUnknown, ref texGuid, out var pTexture);
             Marshal.Release(pUnknown);

             var texture = Marshal.GetObjectForIUnknown(pTexture) as ID3D11Texture2D;
             Marshal.Release(pTexture);

             if (texture == null) return null;

             var desc = texture.Description;
             var width = desc.Width;
             var height = desc.Height;
             
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
        catch
        {
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