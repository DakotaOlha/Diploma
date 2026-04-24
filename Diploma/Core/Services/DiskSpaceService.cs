using System.IO;

namespace Diploma.Core.Services;

public class DiskSpaceCheckResult
{
    public long FreeBytes { get; init; }
    public double FreeGigabytes => FreeBytes / (1024.0 * 1024.0 * 1024.0);
    public double EstimatedHours { get; init; }
    public bool HasEnoughSpace { get; init; }
    public string DriveName { get; init; } = string.Empty;
}

public class DiskSpaceService
{
    private const double VideoMbps = 3.0;
    private const double AudioMbps = 0.35;
    private const double TotalMbps = VideoMbps + AudioMbps;

    private const double MinHoursThreshold = 1.0;

    public DiskSpaceCheckResult Check(string outputPath)
    {
        var root = Path.GetPathRoot(outputPath)
                   ?? Path.GetPathRoot(
                       Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))
                   ?? "C:\\";

        var drive = new DriveInfo(root);
        var freeBytes = drive.AvailableFreeSpace;

        var bytesPerSecond = (TotalMbps * 1_000_000) / 8.0;
        var estimatedSeconds = freeBytes / bytesPerSecond;
        var estimatedHours = estimatedSeconds / 3600.0;

        return new DiskSpaceCheckResult
        {
            FreeBytes      = freeBytes,
            EstimatedHours = estimatedHours,
            HasEnoughSpace = estimatedHours >= MinHoursThreshold,
            DriveName      = root
        };
    }

    public string FormatFreeSpace(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
            >= 1024L * 1024        => $"{bytes / (1024.0 * 1024):F0} MB",
            _                      => $"{bytes / 1024.0:F0} KB"
        };
    }
}