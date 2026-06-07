using Diploma.Core.Models;
using Diploma.Core.Services;
using Diploma.Data.Database;

namespace AlgoReplay.Tests;

public class LogServiceTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly LogService _sut;

    public LogServiceTests()
    {
        _dbPath = Path.GetTempFileName();
        var connStr = $"Data Source={_dbPath}";
        new DatabaseInitializer(connStr).Initialize();
        _sut = new LogService(connStr);
    }

    [Fact]
    public async Task StartSessionAsync_ReturnsPositiveId()
    {
        var id = await _sut.StartSessionAsync(
            "Test", RecordingMode.Personal, "video.mp4");
        Assert.True(id > 0);
    }

    [Fact]
    public async Task LogEventAsync_EventAppearsAfterFlush()
    {
        var sid = await _sut.StartSessionAsync(
            "Test", RecordingMode.Olympic, "v.mp4");
        _sut.AdjustSessionStart(sid, DateTime.UtcNow.AddMinutes(-5));

        await _sut.LogEventAsync(sid,
            EventTypes.FileSave, "Saved solution.cpp");

        await Task.Delay(6_000); // чекаємо flush-таймер (5s)

        var entries = await _sut.GetEntriesAsync(sid);
        Assert.Single(entries);
        Assert.Equal(EventTypes.FileSave, entries[0].EventType);
    }

    [Fact]
    public async Task LogEventAsync_OffsetCalculatedCorrectly()
    {
        var sid = await _sut.StartSessionAsync(
            "Off", RecordingMode.Personal, "v.mp4");
        _sut.AdjustSessionStart(sid, DateTime.UtcNow.AddSeconds(-120));

        await _sut.LogEventAsync(sid,
            EventTypes.RunOrDebug, "F5 pressed");

        await Task.Delay(6_000);

        var entries = await _sut.GetEntriesAsync(sid);
        Assert.InRange(entries[0].Offset.TotalSeconds, 118.0, 125.0);
    }

    [Fact]
    public async Task DeleteSessionAsync_CascadesEntries()
    {
        var sid = await _sut.StartSessionAsync(
            "Del", RecordingMode.Work, "v.mp4");
        await _sut.LogEventAsync(sid,
            EventTypes.ManualMarker, "checkpoint");

        await Task.Delay(6_000);

        await _sut.DeleteSessionAsync(sid);

        var entries = await _sut.GetEntriesAsync(sid);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task ManualMarker_ImmediateFlushWithoutDelay()
    {
        var sid = await _sut.StartSessionAsync(
            "Imm", RecordingMode.Personal, "v.mp4");

        await _sut.LogEventAsync(sid,
            EventTypes.ManualMarker, "urgent");

        await Task.Delay(300); // не чекаємо 5s — має вже бути в БД

        var entries = await _sut.GetEntriesAsync(sid);
        Assert.Single(entries);
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}