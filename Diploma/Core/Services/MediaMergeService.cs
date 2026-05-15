using System.IO;
using FFMpegCore;
using Serilog;

namespace Diploma.Core.Services;

public sealed class MediaMergeService
{
    private const int MergeTimeoutMs = 90_000;

    public async Task<bool> MergeAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        CancellationToken ct = default)
    {
        try
        {
            Log.Information("MediaMerge: starting → {Out}", outputPath);

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(MergeTimeoutMs);

            var success = await FFMpegArguments
                .FromFileInput(videoPath, verifyExists: true)
                .AddFileInput(audioPath, verifyExists: true)
                .OutputToFile(outputPath, overwrite: true, opts => opts
                    .WithVideoCodec("libx264")
                    .WithCustomArgument("-preset ultrafast")
                    .WithConstantRateFactor(28)
                    .WithCustomArgument("-pix_fmt yuv420p")
                    .WithCustomArgument("-vf setpts=PTS-STARTPTS")
                    .WithCustomArgument("-map 0:v:0")
                    .WithCustomArgument("-map 1:a:0")
                    .WithAudioCodec("aac")
                    .WithAudioBitrate(192)
                    .WithCustomArgument("-af apad")
                    .WithCustomArgument("-video_track_timescale 90000")
                    .WithCustomArgument("-movflags +faststart"))
                .CancellableThrough(timeoutCts.Token)
                .ProcessAsynchronously(throwOnError: false);

            if (!success)
            {
                Log.Warning("MediaMerge: FFmpeg returned non-zero");
                TryDelete(outputPath);
                return false;
            }

            var fi = new FileInfo(outputPath);
            if (!fi.Exists || fi.Length < 65_536)
            {
                Log.Warning("MediaMerge: output missing or suspiciously small ({Bytes} bytes)",
                    fi.Exists ? fi.Length : 0);
                TryDelete(outputPath);
                return false;
            }

            if (!IsMp4Valid(outputPath))
            {
                Log.Warning("MediaMerge: merged file has no moov atom — corrupt");
                TryDelete(outputPath);
                return false;
            }

            Log.Information("MediaMerge: done ({Bytes:N0} bytes) → {Out}",
                fi.Length, outputPath);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Warning("MediaMerge: cancelled by caller");
            TryDelete(outputPath);
            return false;
        }
        catch (OperationCanceledException)
        {
            Log.Warning("MediaMerge: timeout after {Ms} ms", MergeTimeoutMs);
            TryDelete(outputPath);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MediaMerge: unexpected error");
            TryDelete(outputPath);
            return false;
        }
    }

    public static bool CanMerge(string videoPath, string audioPath)
    {
        if (!File.Exists(audioPath) || new FileInfo(audioPath).Length == 0)
            return false;

        return IsMp4Valid(videoPath);
    }

    public static bool IsMp4Valid(string path)
    {
        if (!File.Exists(path)) return false;

        var fi = new FileInfo(path);
        if (fi.Length < 8) return false;

        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            Span<byte> header = stackalloc byte[8];

            while (fs.Position + 8 <= fs.Length)
            {
                if (fs.Read(header) < 8) break;

                uint size = ((uint)header[0] << 24)
                           | ((uint)header[1] << 16)
                           | ((uint)header[2] <<  8)
                           |  (uint)header[3];

                string box = System.Text.Encoding.ASCII.GetString(header[4..8]);

                if (box == "moov")
                {
                    Log.Debug("MediaMerge: moov at offset {Off}", fs.Position - 8);
                    return true;
                }

                if (size == 1)
                {
                    Span<byte> ext = stackalloc byte[8];
                    if (fs.Read(ext) < 8) break;
                    ulong extSize = 0;
                    for (int i = 0; i < 8; i++) extSize = (extSize << 8) | ext[i];
                    long skip = (long)extSize - 16;
                    if (skip < 0) break;
                    fs.Seek(skip, SeekOrigin.Current);
                }
                else if (size == 0)
                {
                    break;
                }
                else
                {
                    long skip = (long)size - 8;
                    if (skip < 0) break;
                    fs.Seek(skip, SeekOrigin.Current);
                }
            }

            Log.Warning("MediaMerge: no moov atom in {Path}", path);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MediaMerge: IsMp4Valid scan failed for {Path}", path);
            return false;
        }
    }
    
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex)
        { Log.Warning(ex, "MediaMerge: failed to delete {Path}", path); }
    }
}