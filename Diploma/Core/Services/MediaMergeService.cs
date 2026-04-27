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

            var mergeTask = FFMpegArguments
                .FromFileInput(videoPath, verifyExists: true)
                .AddFileInput(audioPath, verifyExists: true)
                .OutputToFile(outputPath, overwrite: true, opts => opts
                    .CopyChannel(Channel.Video)
                    .WithCustomArgument("-map 0:v:0")
                    .WithCustomArgument("-map 1:a:0")
                    .WithAudioCodec("aac")
                    .WithAudioBitrate(192)
                    .WithCustomArgument("-shortest"))
                .ProcessAsynchronously();

            var completed = await Task.WhenAny(mergeTask, Task.Delay(MergeTimeoutMs, ct));

            if (completed != mergeTask)
            {
                Log.Warning("MediaMerge: timeout after {Ms}ms", MergeTimeoutMs);
                return false;
            }

            var success = await mergeTask;
            
            if (success)
                Log.Information("MediaMerge: done → {Output}", outputPath);
            else
                Log.Warning("MediaMerge: FFmpeg returned failure");

            return success;
        }
        catch (OperationCanceledException)
        {
            Log.Warning("MediaMerge: cancelled");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MediaMerge: unexpected error");
            return false;
        }
    }

    public static bool CanMerge(string videoPath, string audioPath)
        => File.Exists(videoPath) && new FileInfo(videoPath).Length > 0
        && File.Exists(audioPath) && new FileInfo(audioPath).Length > 0;
}