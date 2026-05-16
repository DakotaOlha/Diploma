using System.Buffers;
using System.Diagnostics;
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

public enum CaptureQuality { Low, Medium, High }

public sealed class CaptureQualityProfile
{
    public CaptureQuality Quality      { get; init; }
    public string         Label        { get; init; } = string.Empty;
    public int            Fps          { get; init; }
    public int            Crf          { get; init; }
    public string         EncoderPreset { get; init; } = string.Empty;
    public int            DownscaleWidth { get; init; }

    public static CaptureQualityProfile Get(CaptureQuality q) => q switch
    {
        CaptureQuality.Low  => Low,
        CaptureQuality.High => High,
        _                   => Medium,
    };

    public static readonly CaptureQualityProfile Low = new()
    {
        Quality        = CaptureQuality.Low,
        Label          = "Low — 15 fps, 720p",
        Fps            = 15,
        Crf            = 30,
        EncoderPreset  = "ultrafast",
        DownscaleWidth = 1280,
    };

    public static readonly CaptureQualityProfile Medium = new()
    {
        Quality        = CaptureQuality.Medium,
        Label          = "Medium — 24 fps, native",
        Fps            = 24,
        Crf            = 26,
        EncoderPreset  = "veryfast",
        DownscaleWidth = 0,
    };

    public static readonly CaptureQualityProfile High = new()
    {
        Quality        = CaptureQuality.High,
        Label          = "High — 30 fps, native",
        Fps            = 30,
        Crf            = 22,
        EncoderPreset  = "fast",
        DownscaleWidth = 0,
    };
}

internal sealed class TimestampedFrame : IVideoFrame, IDisposable
{
    private readonly byte[] _data;
    private readonly int    _byteLength;
    private readonly bool   _pooled;
    private          bool   _disposed;

    public int      Width     { get; }
    public int      Height    { get; }
    public string   Format    => "bgra";
    public TimeSpan Timestamp { get; }

    public TimestampedFrame(byte[] data, int width, int height,
                            TimeSpan timestamp, bool pooled = false)
    {
        _data       = data;
        Width       = width;
        Height      = height;
        _byteLength = width * height * 4;
        Timestamp   = timestamp;
        _pooled     = pooled;
    }

    public void Serialize(Stream pipe) => pipe.Write(_data, 0, _byteLength);
    public Task SerializeAsync(Stream pipe, CancellationToken ct)
        => pipe.WriteAsync(_data, 0, _byteLength, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pooled) ArrayPool<byte>.Shared.Return(_data);
    }
}

internal enum CaptureState { Idle = 0, Prepared = 1, Recording = 2, Stopping = 3 }

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

public sealed class ScreenCaptureService : IScreenCaptureService, IDisposable
{
    private const int  FirstFrameTimeoutMs = 5_000;
    private const long SnapshotIntervalMs  = 200;
    private const int  LogRingCapacity     = 60;
    
    private int _channelCapacity = 90;

    private int _state = (int)CaptureState.Idle;
    private CaptureState State => (CaptureState)Volatile.Read(ref _state);

    private bool TryTransition(CaptureState expected, CaptureState next)
        => Interlocked.CompareExchange(ref _state, (int)next, (int)expected) == (int)expected;

    private string?                     _outputPath;
    private GraphicsCaptureItem?        _captureItem;
    private IDirect3DDevice?            _winrtDevice;
    private Channel<TimestampedFrame>?  _frameChannel;

    private CancellationTokenSource?    _cts;
    private CancellationTokenSource?    _encodeCts;
    private Task?                       _captureTask;
    private Task?                       _encodeTask;
    private TaskCompletionSource<bool>? _stopRequested;

    private CaptureQualityProfile _quality = CaptureQualityProfile.Medium;

    public CaptureQuality Quality
    {
        get => _quality.Quality;
        set => _quality = CaptureQualityProfile.Get(value);
    }

    private readonly Stopwatch _recordingClock = new();
    
    public TimeSpan RecordingElapsed => _recordingClock.Elapsed;

    private readonly object _snapshotLock = new();
    private byte[]? _latestFrame;
    private int     _latestFrameWidth;
    private int     _latestFrameHeight;
    private long    _lastSnapshotTickMs;

    private ID3D11Device?        _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private ID3D11Texture2D?     _stagingTexture;
    private (int W, int H)       _stagingSize;
    private readonly object      _d3dLock = new();

    private long _droppedFramesTotal;

    private readonly object   _logLock = new();
    private readonly string[] _logRing = new string[LogRingCapacity];
    private int               _logHead;
    private int               _logCount;

    private bool _disposed;

    public bool IsRecording => State == CaptureState.Recording;

    public event EventHandler<string>?             StatusChanged;
    public event EventHandler?                     RecordingStarted;
    public event EventHandler?                     CaptureTargetSelected;
    public event EventHandler<DropStatsEventArgs>? DropStatsChanged;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint p);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public async Task PrepareAsync(string outputPath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryTransition(CaptureState.Idle, CaptureState.Prepared))
            throw new InvalidOperationException(
                $"PrepareAsync requires Idle state (current: {State}).");

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
                Log("Picker cancelled.");
                Interlocked.Exchange(ref _state, (int)CaptureState.Idle);
                throw new OperationCanceledException("Capture target picker was cancelled.");
            }

            ct.ThrowIfCancellationRequested();

            Log("Initialising D3D device…");
            var winrtDevice = CreateD3DDevice();

            var channel = Channel.CreateBounded<TimestampedFrame>(
                new BoundedChannelOptions(_channelCapacity)
                {
                    FullMode     = BoundedChannelFullMode.DropOldest,
                    SingleWriter = true,
                    SingleReader = true,
                });

            _outputPath   = outputPath;
            _captureItem  = item;
            _winrtDevice  = winrtDevice;
            _frameChannel = channel;

            _droppedFramesTotal    = 0;

            Log($"Prepared — quality={_quality.Label}");
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

        _channelCapacity = _quality.Fps * 3;
        
        if (!TryTransition(CaptureState.Prepared, CaptureState.Recording))
            throw new InvalidOperationException(
                $"BeginCaptureAsync requires Prepared state (current: {State}).");

        if (_captureItem is null || _frameChannel is null || _outputPath is null)
        {
            Interlocked.Exchange(ref _state, (int)CaptureState.Idle);
            throw new InvalidOperationException("Call PrepareAsync first.");
        }

        ct.ThrowIfCancellationRequested();

        _stopRequested = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _cts       = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _encodeCts = new CancellationTokenSource();

        Log("Beginning capture…");

        _captureTask = Task.Run(
            () => CaptureLoopAsync(_captureItem, _outputPath, _winrtDevice!, _cts.Token),
            _cts.Token);

        var startCheck = await Task.WhenAny(_captureTask, Task.Delay(100, _cts.Token))
                                   .ConfigureAwait(false);

        if (startCheck == _captureTask)
        {
            try { await _captureTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"Capture startup failed: {ex.Message}");
                await StopAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    public async Task StopAsync()
    {
        var prev = (CaptureState)Interlocked.CompareExchange(
            ref _state, (int)CaptureState.Stopping, (int)CaptureState.Recording);

        if (prev == CaptureState.Idle || prev == CaptureState.Stopping) return;

        if (prev == CaptureState.Prepared)
            Interlocked.Exchange(ref _state, (int)CaptureState.Stopping);

        Log("Stop requested…");
        _recordingClock.Stop();

        _stopRequested?.TrySetResult(true);
        _cts?.Cancel();

        if (_captureTask is not null)
        {
            try   { await _captureTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"CaptureTask error: {ex.Message}"); }
        }

        if (_encodeTask is not null)
        {
            var finished = await Task.WhenAny(_encodeTask, Task.Delay(10_000))
                .ConfigureAwait(false);

            if (finished != _encodeTask)
            {
                Log("Encode did not finish in 10 s — forcing cancel.");
                _encodeCts?.Cancel();
            }

            try   { await _encodeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"EncodeTask error: {ex.Message}"); }
        }

        ResetD3DState();
        CleanupPrepareContext();

        _cts?.Dispose();       _cts       = null;
        _encodeCts?.Dispose(); _encodeCts = null;

        Interlocked.Exchange(ref _state, (int)CaptureState.Idle);
        Log("Recording stopped — Idle.");
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

            int encodeWidth  = width;
            int encodeHeight = height;
            if (_quality.DownscaleWidth > 0 && width > _quality.DownscaleWidth)
            {
                encodeWidth  = _quality.DownscaleWidth;
                encodeHeight = (int)Math.Round(height * (double)_quality.DownscaleWidth / width);
                encodeWidth  = encodeWidth  & ~1;
                encodeHeight = encodeHeight & ~1;
                Log($"Downscale: {width}×{height} → {encodeWidth}×{encodeHeight}");
            }

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

                    var ts = _recordingClock.IsRunning
                        ? _recordingClock.Elapsed
                        : TimeSpan.Zero;

                    var bytes = ConvertFrameToBytes(frame, width, height,
                                                   encodeWidth, encodeHeight);
                    if (bytes is null) return;

                    var tsFrame = new TimestampedFrame(
                        bytes, encodeWidth, encodeHeight, ts, pooled: true);

                    if (!writer.TryWrite(tsFrame))
                    {
                        Interlocked.Increment(ref _droppedFramesTotal);
                        tsFrame.Dispose();
                    }
                };

                session.StartCapture();
            });

            using var firstFrameCts = new CancellationTokenSource(FirstFrameTimeoutMs);
            using var linked        = CancellationTokenSource.CreateLinkedTokenSource(
                                          token, firstFrameCts.Token);
            bool gotFirst = false;
            try   { gotFirst = await _frameChannel!.Reader.WaitToReadAsync(linked.Token); }
            catch (OperationCanceledException) { }

            if (!gotFirst || token.IsCancellationRequested)
            {
                Log("No frames within timeout — aborting.");
                return;
            }

            _recordingClock.Restart();
            Log("First frame ready — encode clock started.");

            RecordingStarted?.Invoke(this, EventArgs.Empty);

            _encodeTask = Task.Factory.StartNew(
                () => EncodeLoopAsync(outputPath, encodeWidth, encodeHeight, _encodeCts!.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();

            try
            {
                await Task.WhenAny(
                    _stopRequested!.Task,
                    Task.Delay(Timeout.Infinite, token));
            }
            catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException) { Log("CaptureLoop cancelled."); }
        catch (Exception ex)               { Log($"Capture error: {ex.GetType().Name}: {ex.Message}"); }
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

    private async Task EncodeLoopAsync(string outputPath, int width, int height, CancellationToken ct)
    {
        // Фіксуємо FPS один раз — не змінюється протягом запису
        int fixedFps = _quality.Fps;
 
        // Окрема черга між frame-pacer і FFmpeg pipe writer.
        // Wait (не DropOldest) — якщо FFmpeg відстає, pacer чекає,
        // але не пропускає кадри в timeline відео.
        // Розмір: FPS * 2 секунди — достатній буфер для encoder spikes.
        var encodeChannel = Channel.CreateBounded<TimestampedFrame>(
            new BoundedChannelOptions(fixedFps * 2)
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = true,
            });
 
        // Frame pacer: окремий LongRunning thread.
        // Відповідальність: читати з _frameChannel і писати в encodeChannel
        // з точним timing (fixedFps). НЕ виконує IO до FFmpeg pipe.
        var pacerTask = Task.Factory.StartNew(() =>
        {
            timeBeginPeriod(1);
 
            long frameDurationTicks = Stopwatch.Frequency / fixedFps;
            long nextTick           = Stopwatch.GetTimestamp();
            TimestampedFrame? current = null;
 
            try
            {
                var reader = _frameChannel!.Reader;
 
                while (!ct.IsCancellationRequested)
                {
                    // Виходимо якщо capture завершився і черга порожня
                    if (reader.Completion.IsCompleted && reader.Count == 0)
                        break;
 
                    // Зчитуємо всі доступні кадри — беремо тільки найсвіжіший
                    while (reader.TryRead(out var candidate))
                    {
                        current?.Dispose();
                        current = candidate;
                    }
 
                    long now  = Stopwatch.GetTimestamp();
                    long wait = nextTick - now;
 
                    if (wait > 0)
                    {
                        // Гібридне очікування: Sleep для великих інтервалів + spin для точності
                        int sleepMs = (int)(wait * 1000L / Stopwatch.Frequency) - 1;
                        if (sleepMs > 1)
                            Thread.Sleep(sleepMs);
 
                        // Spin-wait для останнього ~1ms без yield
                        while (Stopwatch.GetTimestamp() < nextTick)
                        {
                            // Якщо залишилось > 0.5ms — відпускаємо timeslice
                            if (nextTick - Stopwatch.GetTimestamp() >
                                Stopwatch.Frequency / 2000)
                                Thread.Sleep(0);
                        }
                    }
 
                    // Рухаємо cursor вперед на один frame slot
                    nextTick += frameDurationTicks;
 
                    // Антидрейф: якщо накопичили борг більше 2 кадрів — скидаємо.
                    // Це відбувається після довгого GC або OS scheduling jitter.
                    now = Stopwatch.GetTimestamp();
                    if (nextTick < now - frameDurationTicks * 2)
                    {
                        Log($"Pacer: clock drift detected, resetting. " +
                            $"Debt={(now - nextTick) * 1000 / Stopwatch.Frequency}ms");
                        nextTick = now;
                    }
 
                    // Якщо кадру ще немає — чекаємо наступного slot без запису.
                    // FFmpeg отримає той самий кадр ще раз (freeze-frame) — це OK.
                    if (current == null) continue;
 
                    // Записуємо в encodeChannel.
                    // Якщо channel повний (FFmpeg відстає) — чекаємо (Wait mode).
                    // Використовуємо синхронний write через TryWrite з fallback.
                    if (!encodeChannel.Writer.TryWrite(current))
                    {
                        // Channel тимчасово повний — робимо sync wait
                        encodeChannel.Writer.WriteAsync(current, ct)
                            .AsTask().GetAwaiter().GetResult();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"Pacer error: {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                // НЕ dispose current — він вже у encodeChannel або буде dispose нижче
                encodeChannel.Writer.TryComplete();
                timeEndPeriod(1);
                Log("Frame pacer finished.");
            }
 
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
 
        // FFmpeg frame source: читає з encodeChannel без будь-якого timing.
        // Весь timing контролюється pacer thread вище.
        // Цей метод виконується в LongRunning encode thread.
        IEnumerable<IVideoFrame> FrameSource()
        {
            var reader = encodeChannel.Reader;
            while (true)
            {
                // Блокуємо поки є дані або channel не закрито
                bool hasData;
                try
                {
                    hasData = reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { break; }
 
                if (!hasData) break;
 
                while (reader.TryRead(out var frame))
                    yield return frame;
            }
        }
 
        var videoSource = new RawVideoPipeSource(FrameSource()) { FrameRate = fixedFps };
 
        try
        {
            Log($"FFmpeg encode started ({width}×{height} @ {fixedFps} fps, " +
                $"encodeBuffer={fixedFps * 2} frames)…");
            var encoder = HardwareEncoderDetector.Detect();
 
            var encodeArgs = FFMpegArguments
                .FromPipeInput(videoSource, opts => opts
                    .ForceFormat("rawvideo")
                    .WithCustomArgument($"-pix_fmt bgra -s {width}x{height} -r {fixedFps}"))
                .OutputToFile(outputPath, overwrite: true, opts =>
                {
                    opts.WithVideoCodec(encoder)
                        .WithCustomArgument("-pix_fmt yuv420p")
                        .WithCustomArgument($"-r {fixedFps}")
                        .WithCustomArgument("-fps_mode cfr");
 
                    ApplyEncoderOptions(opts, encoder);
                })
                .CancellableThrough(ct);
 
            var ok = await encodeArgs.ProcessAsynchronously(throwOnError: false);
 
            Log(ok ? "Encode complete — MP4 fully written."
                   : "Encode completed with FFmpeg warnings.");
 
            var total = Interlocked.Read(ref _droppedFramesTotal);
            if (total > 0) Log($"Total frames dropped by WinRT channel: {total}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Encode error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Чекаємо завершення pacer перед виходом
            try { await pacerTask.ConfigureAwait(false); }
            catch { }
        }
    }

    private void ApplyEncoderOptions(FFMpegArgumentOptions opts, string encoder)
    {
        int    crf    = _quality.Crf;
        string preset = _quality.EncoderPreset;

        switch (encoder)
        {
            case "libx264":
                opts.WithConstantRateFactor(crf)
                    .WithCustomArgument($"-preset {preset}")
                    .WithCustomArgument("-tune zerolatency");
                break;

            case "h264_nvenc":
                int nvQp = Math.Clamp(crf, 18, 35);
                opts.WithCustomArgument("-preset p1")
                    .WithCustomArgument("-rc vbr")
                    .WithCustomArgument($"-cq {nvQp}")
                    .WithCustomArgument("-b:v 0");
                break;

            case "h264_amf":
                int amfQp = Math.Clamp(crf, 18, 35);
                opts.WithCustomArgument("-quality speed")
                    .WithCustomArgument($"-rc cqp -qp_i {amfQp} -qp_p {amfQp}");
                break;

            case "h264_qsv":
                opts.WithCustomArgument($"-preset {preset}")
                    .WithCustomArgument($"-global_quality {crf}");
                break;

            default:
                opts.WithConstantRateFactor(crf)
                    .WithCustomArgument("-preset ultrafast");
                break;
        }
    }

    public async Task<string?> TakeScreenshotAsync(string outputDir)
    {
        byte[]? snapshot;
        int w, h;

        lock (_snapshotLock)
        {
            snapshot = _latestFrame;
            w        = _latestFrameWidth;
            h        = _latestFrameHeight;
        }

        if (snapshot is null || w == 0 || h == 0) return null;

        var ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var path = Path.Combine(outputDir, $"screenshot_{ts}.png");

        await Task.Run(() => SaveBgraToPng(snapshot, w, h, path));
        return path;
    }

    public BitmapSource? GetLatestFrameAsBitmap()
    {
        byte[]? snapshot;
        int w, h;

        lock (_snapshotLock)
        {
            snapshot = _latestFrame;
            w        = _latestFrameWidth;
            h        = _latestFrameHeight;
        }

        if (snapshot is null || w == 0 || h == 0) return null;

        return BitmapSource.Create(w, h, 96, 96,
            PixelFormats.Bgra32, null, snapshot, w * 4);
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

        using var dxgi = device.QueryInterface<IDXGIDevice>();
        CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var pDev);
        var winrt = MarshalInterface<IDirect3DDevice>.FromAbi(pDev);
        Marshal.Release(pDev);
        return winrt;
    }

    private byte[]? ConvertFrameToBytes(
        Direct3D11CaptureFrame frame,
        int nativeW, int nativeH,
        int encodeW, int encodeH)
    {
        lock (_d3dLock)
        {
            if (_d3dDevice is null || _d3dContext is null) return null;

            try
            {
                var access     = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
                var texturePtr = access.GetInterface(typeof(ID3D11Texture2D).GUID);
                if (texturePtr == IntPtr.Zero) return null;

                using var texture = new ID3D11Texture2D(texturePtr);
                var desc = texture.Description;

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
                }

                _d3dContext.CopyResource(_stagingTexture, texture);
                var mapped = _d3dContext.Map(_stagingTexture, 0, MapMode.Read, MapFlags.None);

                try
                {
                    bool needScale = encodeW != nativeW || encodeH != nativeH;
                    int  outBytes  = encodeW * encodeH * 4;
                    var  outBuf    = ArrayPool<byte>.Shared.Rent(outBytes);

                    unsafe
                    {
                        byte* src = (byte*)mapped.DataPointer;

                        if (!needScale)
                        {
                            for (int y = 0; y < nativeH; y++)
                                new ReadOnlySpan<byte>(src + y * mapped.RowPitch, nativeW * 4)
                                    .CopyTo(new Span<byte>(outBuf, y * nativeW * 4, nativeW * 4));
                        }
                        else
                        {
                            double xRatio = (double)nativeW / encodeW;
                            double yRatio = (double)nativeH / encodeH;
                            for (int oy = 0; oy < encodeH; oy++)
                            {
                                int   sy     = (int)(oy * yRatio);
                                byte* srcRow = src + sy * mapped.RowPitch;
                                int   outOff = oy * encodeW * 4;
                                for (int ox = 0; ox < encodeW; ox++)
                                {
                                    int inOff = (int)(ox * xRatio) * 4;
                                    outBuf[outOff]     = srcRow[inOff];
                                    outBuf[outOff + 1] = srcRow[inOff + 1];
                                    outBuf[outOff + 2] = srcRow[inOff + 2];
                                    outBuf[outOff + 3] = srcRow[inOff + 3];
                                    outOff += 4;
                                }
                            }
                        }
                    }

                    var nowMs = Environment.TickCount64;
                    if (nowMs - _lastSnapshotTickMs >= SnapshotIntervalMs)
                    {
                        var snap = new byte[outBytes];
                        Buffer.BlockCopy(outBuf, 0, snap, 0, outBytes);
                        lock (_snapshotLock)
                        {
                            _latestFrame       = snap;
                            _latestFrameWidth  = encodeW;
                            _latestFrameHeight = encodeH;
                        }
                        _lastSnapshotTickMs = nowMs;
                    }

                    return outBuf;
                }
                finally
                {
                    _d3dContext.Unmap(_stagingTexture, 0);
                }
            }
            catch (Exception ex)
            {
                Log($"Frame conversion error: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    private void ResetD3DState()
    {
        lock (_d3dLock)
        {
            _stagingTexture?.Dispose(); _stagingTexture = null;
            _stagingSize = default;
            _d3dContext?.Dispose();     _d3dContext = null;
            _d3dDevice?.Dispose();      _d3dDevice  = null;
        }
    }

    private void CleanupPrepareContext()
    {
        _outputPath    = null;
        _captureItem   = null;
        _winrtDevice   = null;
        _frameChannel?.Writer.TryComplete();
        _frameChannel  = null;
        _stopRequested = null;
        _captureTask   = null;
        _encodeTask    = null;
    }

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} | {message}";
        string snap;

        lock (_logLock)
        {
            _logRing[_logHead] = line;
            _logHead  = (_logHead + 1) % LogRingCapacity;
            _logCount = Math.Min(_logCount + 1, LogRingCapacity);

            int count = _logCount;
            int start = count < LogRingCapacity ? 0 : _logHead;
            var sb    = new StringBuilder(count * 80);
            for (int i = 0; i < count; i++)
                sb.AppendLine(_logRing[(start + i) % LogRingCapacity]);
            snap = sb.ToString();
        }

        StatusChanged?.Invoke(this, snap);
    }

    private void RaiseDropStats(long total, double rate, int fps)
    {
        var args = new DropStatsEventArgs(total, rate, fps);
        Task.Run(() => DropStatsChanged?.Invoke(this, args));
    }

    private static void SaveBgraToPng(byte[] data, int w, int h, string path)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96,
            PixelFormats.Bgra32, null, data, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _recordingClock.Stop();
        _stopRequested?.TrySetResult(true);
        _cts?.Cancel();
        _encodeCts?.Cancel();

        _captureTask?.Wait(TimeSpan.FromSeconds(2));
        _encodeTask?.Wait(TimeSpan.FromSeconds(30));

        _cts?.Dispose();
        _encodeCts?.Dispose();

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