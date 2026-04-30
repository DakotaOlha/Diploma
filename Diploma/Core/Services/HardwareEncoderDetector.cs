using System.IO;
using FFMpegCore;
using FFMpegCore.Pipes;
using Serilog;

namespace Diploma.Core.Services;

public static class HardwareEncoderDetector
{
    private static readonly string[] CandidateEncoders =
    [
        "h264_nvenc",
        "h264_amf",
        "h264_qsv"
    ];

    private const string SoftwareFallback = "libx264";

    private static string? _cached;
    private static readonly object _lock = new();

    public static string Detect()
    {
        if (_cached is not null) return _cached;

        lock (_lock)
        {
            if (_cached is not null) return _cached;

            _cached = ProbeEncoders();
            Log.Information("HardwareEncoderDetector: selected encoder = {Encoder}", _cached);
            return _cached;
        }
    }

    private static string ProbeEncoders()
    {
        foreach (var encoder in CandidateEncoders)
        {
            if (TryEncoder(encoder))
            {
                Log.Information("HardwareEncoderDetector: {Encoder} available", encoder);
                return encoder;
            }

            Log.Debug("HardwareEncoderDetector: {Encoder} not available", encoder);
        }

        Log.Information("HardwareEncoderDetector: falling back to {Encoder}", SoftwareFallback);
        return SoftwareFallback;
    }

    private static bool TryEncoder(string encoder)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"hwenc_probe_{encoder}_{Guid.NewGuid():N}.mp4");

        try
        {
            const int W = 2;
            const int H = 2;
            var blackFrame = new BlackFrameSource(W, H);

            var args = FFMpegArguments
                .FromPipeInput(blackFrame, opts => opts
                    .WithVideoCodec("rawvideo")
                    .ForceFormat("rawvideo")
                    .WithCustomArgument($"-pix_fmt bgra -s {W}x{H} -r 1"))
                .OutputToFile(tmp, overwrite: true, opts =>
                {
                    opts.WithVideoCodec(encoder)
                        .WithCustomArgument("-frames:v 1");

                    if (encoder == "h264_nvenc")
                        opts.WithCustomArgument("-preset p1");
                    else if (encoder == "h264_amf")
                        opts.WithCustomArgument("-quality speed");
                    else if (encoder == "h264_qsv")
                        opts.WithCustomArgument("-preset veryfast");
                });

            var ok = args.ProcessSynchronously(throwOnError: false);
            return ok;
        }
        catch (Exception ex)
        {
            Log.Debug("HardwareEncoderDetector: {Encoder} probe threw {Ex}", encoder, ex.Message);
            return false;
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch {  }
    }

    private sealed class BlackFrameSource : IPipeSource
    {
        private readonly int _w;
        private readonly int _h;

        private readonly byte[] _data;

        public BlackFrameSource(int w, int h)
        {
            _w = w;
            _h = h;
            _data = new byte[w * h * 4];
        }

        public string GetStreamArguments() =>
            $"-f rawvideo -pix_fmt bgra -s {_w}x{_h} -r 1";

        public async Task WriteAsync(Stream outputStream, CancellationToken cancellationToken)
        {
            await outputStream.WriteAsync(_data, cancellationToken);
        }
    }
}