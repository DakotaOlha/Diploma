using System.IO;
using FFMpegCore;
using FFMpegCore.Enums;
using Serilog;

namespace Diploma.Core.Services;

public class MediaMergeService
{
    private const int MergeTimeoutMs = 60_000;

    public async Task<bool> MergeAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        CancellationToken ct = default)
    {
        try
        {
            Log.Information("MediaMerge: starting merge → {Output}", outputPath);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(MergeTimeoutMs);
            
            var success = await FFMpegArguments
                .FromFileInput(videoPath, verifyExists: true)
                .AddFileInput(audioPath, verifyExists: true)
                .OutputToFile(outputPath, overwrite: true, opts => opts
                    .CopyChannel(Channel.Video)
                    .WithCustomArgument("-map 0:v:0")
                    .WithCustomArgument("-map 1:a:0")
                    .WithAudioCodec("aac")
                    .WithAudioBitrate(192)
                    .WithCustomArgument("-shortest"))
                .CancellableThrough(timeoutCts.Token)
                .ProcessAsynchronously(throwOnError: false);

            if (success)
            {
                Log.Information("MediaMerge: done → {Output}", outputPath);
            }
            else
            {
                Log.Warning("MediaMerge: FFmpeg returned failure");
                TryDeleteFile(outputPath);
            }

            return success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Warning("MediaMerge: cancelled by caller");
            TryDeleteFile(outputPath);
            return false;
        }
        catch (OperationCanceledException)
        {
            Log.Warning("MediaMerge: timeout after {Ms}ms", MergeTimeoutMs);
            TryDeleteFile(outputPath);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MediaMerge: unexpected error");
            TryDeleteFile(outputPath);
            return false;
        }
    }
    
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MediaMerge: failed to delete partial file {Path}", path);
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
                           | ((uint)header[2] << 8)
                           |  (uint)header[3];

                string boxType = System.Text.Encoding.ASCII.GetString(header[4..8]);

                if (boxType == "moov")
                {
                    Log.Debug("MediaMerge: moov atom found at offset {Offset}", fs.Position - 8);
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

            Log.Warning("MediaMerge: no moov atom found in {Path} — file is corrupt", path);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MediaMerge: IsMp4Valid scan failed for {Path}", path);
            return false;
        }
    }
}