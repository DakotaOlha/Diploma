using System.Buffers;
using System.Collections.Concurrent;
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

    // Diagnostic counters — reset at each recording start, flushed to _diag.txt
    private long _diagFramesArrived;
    private long _diagFreezeFrames;
    private long _diagCatchUpIter;
    private long _diagOnSchedIter;
    private long _diagBackpressure;
    private long _diagFramesSent;
    private long _diagSleepOverruns;
    private long _diagMaxDebtMs;

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
                new BoundedChannelOptions(_quality.Fps * 3)
                {
                    FullMode     = BoundedChannelFullMode.DropOldest,
                    SingleWriter = true,
                    SingleReader = true,
                });

            _outputPath   = outputPath;
            _captureItem  = item;
            _winrtDevice  = winrtDevice;
            _frameChannel = channel;

            _droppedFramesTotal = 0;
            _diagFramesArrived  = 0;
            _diagFreezeFrames   = 0;
            _diagCatchUpIter    = 0;
            _diagOnSchedIter    = 0;
            _diagBackpressure   = 0;
            _diagFramesSent     = 0;
            _diagSleepOverruns  = 0;
            _diagMaxDebtMs      = 0;

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

                    Interlocked.Increment(ref _diagFramesArrived);

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
        int fixedFps = _quality.Fps;

        var encodeChannel = Channel.CreateBounded<TimestampedFrame>(
            new BoundedChannelOptions(fixedFps * 4)
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = true,
            });

        // ── Diagnostic setup ─────────────────────────────────────────────────
        var diagPath     = Path.ChangeExtension(outputPath, null) + "_diag.txt";
        var diagSw       = Stopwatch.StartNew();
        var diagQueue    = new ConcurrentQueue<string>();
        var diagStatsCts = new CancellationTokenSource();

        Action<string> DiagEvent = msg =>
            diagQueue.Enqueue($"{diagSw.Elapsed:mm\\:ss\\.fff} | {msg}");

        DiagEvent("=== AlgoReplay Pipeline Diagnostic Log ===");
        DiagEvent($"Quality={_quality.Label}  FPS={fixedFps}  CapChan={_quality.Fps * 3}  EncChan={fixedFps * 4}  Video={width}x{height}");
        DiagEvent("");
        DiagEvent("Each STATS line = 1-second window");
        DiagEvent("  ARR    = WinRT frames captured");
        DiagEvent("  DROP   = dropped at capture channel (pacer too slow)");
        DiagEvent("  SENT   = frames written to FFmpeg");
        DiagEvent("  FREEZE = slots where last frame was re-sent (static screen or OS stall)");
        DiagEvent("  ONTIME = pacer iterations that slept (on schedule)");
        DiagEvent("  CATCHUP= pacer iterations that ran without sleep (behind)");
        DiagEvent("  BKPRS  = times FFmpeg encode channel was full (backpressure)");
        DiagEvent("  OVRUN  = Thread.Sleep overruns > 10ms");
        DiagEvent("  DEBT   = max pacer debt in this second (ms behind schedule)");
        DiagEvent(new string('-', 100));

        // Per-second stats reporter
        var statsTask = Task.Run(async () =>
        {
            long pArr = 0, pDrop = 0, pSent = 0, pFreeze = 0;
            long pOn = 0, pCu = 0, pBp = 0, pOv = 0;
            try
            {
                while (!diagStatsCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, diagStatsCts.Token);

                    long a  = Interlocked.Read(ref _diagFramesArrived);
                    long d  = Interlocked.Read(ref _droppedFramesTotal);
                    long s  = Interlocked.Read(ref _diagFramesSent);
                    long f  = Interlocked.Read(ref _diagFreezeFrames);
                    long on = Interlocked.Read(ref _diagOnSchedIter);
                    long cu = Interlocked.Read(ref _diagCatchUpIter);
                    long bp = Interlocked.Read(ref _diagBackpressure);
                    long ov = Interlocked.Read(ref _diagSleepOverruns);
                    long mx = Interlocked.Exchange(ref _diagMaxDebtMs, 0);

                    diagQueue.Enqueue(
                        $"STATS {diagSw.Elapsed:mm\\:ss} | " +
                        $"ARR={a-pArr,4} DROP={d-pDrop,3} SENT={s-pSent,4} " +
                        $"FREEZE={f-pFreeze,5} ONTIME={on-pOn,4} CATCHUP={cu-pCu,5} " +
                        $"BKPRS={bp-pBp,3} OVRUN={ov-pOv,3} DEBT={mx,6}ms");

                    pArr = a; pDrop = d; pSent = s; pFreeze = f;
                    pOn = on; pCu = cu; pBp = bp; pOv = ov;
                }
            }
            catch (OperationCanceledException) { }
        });

        // Async file writer: drains diagQueue to disk every 250ms
        var writerTask = Task.Run(async () =>
        {
            try
            {
                await using var sw = new StreamWriter(diagPath, append: false, Encoding.UTF8);
                while (true)
                {
                    bool wrote = false;
                    while (diagQueue.TryDequeue(out var line))
                    {
                        await sw.WriteLineAsync(line);
                        wrote = true;
                    }
                    if (wrote) await sw.FlushAsync();

                    if (diagStatsCts.IsCancellationRequested && diagQueue.IsEmpty)
                        break;

                    try { await Task.Delay(250, diagStatsCts.Token); }
                    catch (OperationCanceledException)
                    {
                        while (diagQueue.TryDequeue(out var line))
                            await sw.WriteLineAsync(line);
                        await sw.FlushAsync();
                        break;
                    }
                }
            }
            catch (Exception ex) { Log($"DiagWriter: {ex.Message}"); }
        });
        // ─────────────────────────────────────────────────────────────────────

        // Frame pacer: LongRunning thread, AboveNormal priority, 1ms OS timer.
        // Reads _frameChannel → encodeChannel at exactly fixedFps.
        var pacerTask = Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            timeBeginPeriod(1);

            long frameDurationTicks = Stopwatch.Frequency / fixedFps;
            long nextTick           = Stopwatch.GetTimestamp();
            TimestampedFrame? current   = null;
            TimestampedFrame? freezeRef = null; // last-sent frame, re-used as freeze content

            try
            {
                var reader = _frameChannel!.Reader;

                while (!ct.IsCancellationRequested)
                {
                    if (reader.Completion.IsCompleted && reader.Count == 0)
                        break;

                    long now  = Stopwatch.GetTimestamp();
                    long wait = nextTick - now;

                    if (wait > 0)
                    {
                        // ON SCHEDULE: drain all frames before sleep, keep newest
                        while (reader.TryRead(out var c)) { current?.Dispose(); current = c; }

                        long beforeSleep = Stopwatch.GetTimestamp();
                        int sleepMs = (int)(wait * 1000L / Stopwatch.Frequency) - 1;
                        if (sleepMs > 1)
                            Thread.Sleep(sleepMs);

                        while (Stopwatch.GetTimestamp() < nextTick)
                        {
                            if (nextTick - Stopwatch.GetTimestamp() > Stopwatch.Frequency / 2000)
                                Thread.Sleep(0);
                        }

                        // Detect OS sleep overruns (scheduler gave us back too late)
                        if (sleepMs > 1)
                        {
                            long actualMs   = (Stopwatch.GetTimestamp() - beforeSleep) * 1000L / Stopwatch.Frequency;
                            long expectedMs = wait * 1000L / Stopwatch.Frequency;
                            if (actualMs > expectedMs + 10)
                            {
                                Interlocked.Increment(ref _diagSleepOverruns);
                                DiagEvent($"SLEEP_OVERRUN: expected={expectedMs}ms actual={actualMs}ms overrun={actualMs - expectedMs}ms");
                            }
                        }

                        // Read again post-sleep — fresher content may have arrived
                        while (reader.TryRead(out var c)) { current?.Dispose(); current = c; }

                        Interlocked.Increment(ref _diagOnSchedIter);
                    }
                    else
                    {
                        // CATCH-UP: read ONE frame (FIFO) — spreads accumulated frames
                        // evenly across iterations instead of draining all in one shot
                        // (which would leave subsequent catch-up iterations empty → lost slots)
                        if (reader.TryRead(out var c)) { current?.Dispose(); current = c; }

                        Interlocked.Increment(ref _diagCatchUpIter);
                    }

                    nextTick += frameDurationTicks;

                    now = Stopwatch.GetTimestamp();
                    long debtTicks = now - nextTick;
                    if (debtTicks > Stopwatch.Frequency * 5)
                    {
                        long debtMs = debtTicks * 1000L / Stopwatch.Frequency;
                        Log($"Pacer: extreme debt ({debtMs}ms) — capping to 5 s.");
                        DiagEvent($"EXTREME_DEBT: {debtMs}ms — capped to 5000ms");
                        nextTick = now - Stopwatch.Frequency * 5;
                    }

                    // Track max debt per second for stats
                    long currDebt = Math.Max(0L, (now - nextTick) * 1000L / Stopwatch.Frequency);
                    long prev = Interlocked.Read(ref _diagMaxDebtMs);
                    while (currDebt > prev)
                    {
                        long old = Interlocked.CompareExchange(ref _diagMaxDebtMs, currDebt, prev);
                        if (old == prev) break;
                        prev = old;
                    }

                    if (current == null)
                    {
                        if (freezeRef == null)
                        {
                            // No frame ever received — nothing to repeat yet, skip slot
                            continue;
                        }
                        // WinRT delivered no new frame (static screen or OS stall).
                        // Re-send last known frame to keep video duration correct.
                        current = freezeRef;
                    }

                    bool isFreeze = ReferenceEquals(current, freezeRef);

                    if (!encodeChannel.Writer.TryWrite(current))
                    {
                        Interlocked.Increment(ref _diagBackpressure);
                        DiagEvent($"BACKPRESSURE: encodeChannel full (cap={fixedFps * 4}), pacer blocked on WriteAsync");
                        encodeChannel.Writer.WriteAsync(current, ct).AsTask().GetAwaiter().GetResult();
                    }
                    Interlocked.Increment(ref _diagFramesSent);

                    if (isFreeze)
                    {
                        Interlocked.Increment(ref _diagFreezeFrames);
                        // Keep freezeRef for next iteration — don't null it via current
                    }
                    else
                    {
                        freezeRef = current; // Update last-sent frame for future freeze slots
                    }
                    current = null;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"Pacer error: {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                current?.Dispose();
                encodeChannel.Writer.TryComplete();
                timeEndPeriod(1);
                Log("Frame pacer finished.");
            }

        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        IEnumerable<IVideoFrame> FrameSource()
        {
            var reader = encodeChannel.Reader;
            while (true)
            {
                bool hasData;
                try { hasData = reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { break; }

                if (!hasData) break;

                while (reader.TryRead(out var frame))
                    yield return frame;
            }
        }

        var videoSource = new RawVideoPipeSource(FrameSource()) { FrameRate = fixedFps };

        try
        {
            Log($"FFmpeg encode started ({width}×{height} @ {fixedFps} fps, encodeBuffer={fixedFps * 4} frames)…");
            var encoder = HardwareEncoderDetector.Detect();
            DiagEvent($"Encoder: {encoder}");

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
            try { await pacerTask.ConfigureAwait(false); } catch { }

            // Write final diagnostic summary
            long totArr    = Interlocked.Read(ref _diagFramesArrived);
            long totDrop   = Interlocked.Read(ref _droppedFramesTotal);
            long totSent   = Interlocked.Read(ref _diagFramesSent);
            long totFreeze = Interlocked.Read(ref _diagFreezeFrames);
            long totCu     = Interlocked.Read(ref _diagCatchUpIter);
            long totOn     = Interlocked.Read(ref _diagOnSchedIter);
            long totBp     = Interlocked.Read(ref _diagBackpressure);
            long totOv     = Interlocked.Read(ref _diagSleepOverruns);
            long expected  = (long)(diagSw.Elapsed.TotalSeconds * fixedFps);
            double lostPct = expected > 0 ? (expected - totSent) * 100.0 / expected : 0;

            DiagEvent("");
            DiagEvent("=== FINAL SUMMARY ===");
            DiagEvent($"Diag elapsed (encode loop):   {diagSw.Elapsed:mm\\:ss\\.fff}");
            DiagEvent($"RecordingClock elapsed:        {_recordingClock.Elapsed:mm\\:ss\\.fff}");
            DiagEvent($"Expected frames @ {fixedFps}fps:    {expected}");
            DiagEvent($"Frames sent to FFmpeg:         {totSent}");
            DiagEvent($"Lost frame slots:              {expected - totSent}  ({lostPct:F1}%)");
            DiagEvent($"WinRT frames arrived:          {totArr}");
            DiagEvent($"WinRT frames dropped (chan):   {totDrop}");
            DiagEvent($"Freeze frames sent (repeat):   {totFreeze}  (static screen / OS stall)");
            DiagEvent($"Pacer on-schedule iterations:  {totOn}");
            DiagEvent($"Pacer catch-up iterations:     {totCu}");
            DiagEvent($"FFmpeg backpressure events:    {totBp}");
            DiagEvent($"Sleep overrun events:          {totOv}");

            diagStatsCts.Cancel();
            try { await statsTask.ConfigureAwait(false); } catch { }
            try { await writerTask.ConfigureAwait(false); } catch { }
            diagStatsCts.Dispose();

            Log($"Diagnostic log: {diagPath}");
        }
    }

    private void ApplyEncoderOptions(FFMpegArgumentOptions opts, string encoder)
    {
        int    crf    = _quality.Crf;
        string preset = _quality.EncoderPreset;

        switch (encoder)
        {
            case "libx264":
                // ultrafast є обов'язковим для real-time запису — будь-який інший preset
                // може бути повільнішим за реальний час і блокуватиме pacer.
                // Якість контролюється через CRF, а не preset.
                opts.WithConstantRateFactor(crf)
                    .WithCustomArgument("-preset ultrafast")
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