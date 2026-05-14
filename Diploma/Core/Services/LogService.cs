using System.Collections.Concurrent;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using Dapper;
using Diploma.Data.Database;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Diploma.Core.Services;

public class LogService : ILogService, IAsyncDisposable
{
    private readonly string _connectionString;

    private readonly ConcurrentDictionary<int, DateTime> _sessionStarts = new();

    private sealed record PendingEvent(
        int      SessionId,
        DateTime Timestamp,
        double   OffsetSeconds,
        string   EventType,
        string   Description,
        string?  Metadata);

    private static readonly HashSet<string> ImmediateFlushTypes = new()
    {
        EventTypes.ManualMarker,
        EventTypes.Screenshot,
    };

    private readonly ConcurrentQueue<PendingEvent> _pending = new();
    private readonly Timer  _flushTimer;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private bool _disposed;

    public LogService(string connectionString)
    {
        _connectionString = connectionString;
        
        _flushTimer = new Timer(
            async _ => await FlushAsync(),
            state: null,
            dueTime: TimeSpan.FromSeconds(5),
            period: TimeSpan.FromSeconds(5));
    }

    public async Task<int> StartSessionAsync(string name, RecordingMode mode, string videoFilePath)
    {
        var now = DateTime.UtcNow;

        const string sql = """
            INSERT INTO RecordingSessions (Name, StartTime, Mode, VideoFilePath)
            VALUES (@Name, @StartTime, @Mode, @VideoFilePath);
            SELECT last_insert_rowid();
        """;

        await using var conn = CreateConnection();
        var id = await conn.ExecuteScalarAsync<int>(sql, new
        {
            Name          = name,
            StartTime     = now.ToString("O"),
            Mode          = mode.ToString(),
            VideoFilePath = videoFilePath
        });

        _sessionStarts[id] = now;
        return id;
    }

    public async Task EndSessionAsync(int sessionId)
    {
        await FlushAsync();

        const string sql = """
            UPDATE RecordingSessions
            SET EndTime = @EndTime
            WHERE Id = @Id
        """;

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            EndTime = DateTime.UtcNow.ToString("O"),
            Id      = sessionId
        });

        _sessionStarts.TryRemove(sessionId, out _);
    }

    public async Task DeleteSessionAsync(int sessionId)
    {
        const string sql = "DELETE FROM RecordingSessions WHERE Id = @Id";

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new { Id = sessionId });

        _sessionStarts.TryRemove(sessionId, out _);
    }

    public async Task<RecordingSession?> GetSessionAsync(int sessionId)
    {
        await FlushAsync();

        const string sql = "SELECT * FROM RecordingSessions WHERE Id = @Id";

        await using var conn = CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync(sql, new { Id = sessionId });
        return row is null ? null : MapSession(row);
    }

    public async Task<IReadOnlyList<RecordingSession>> GetAllSessionsAsync()
    {
        await FlushAsync();

        const string sql = "SELECT * FROM RecordingSessions ORDER BY StartTime DESC";

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync(sql);
        return rows.Select(MapSession).ToList();
    }

    public Task LogEventAsync(
        int sessionId, string eventType, string description, string? metadata = null)
    {
        if (_disposed) return Task.CompletedTask;

        var now    = DateTime.UtcNow;
        var offset = _sessionStarts.TryGetValue(sessionId, out var start)
            ? (now - start).TotalSeconds
            : 0.0;

        _pending.Enqueue(new PendingEvent(
            sessionId, now, offset, eventType, description, metadata));

        if (ImmediateFlushTypes.Contains(eventType))
            _ = FlushAsync();

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<LogEntry>> GetEntriesAsync(int sessionId)
    {
        await FlushAsync();

        const string sql = """
            SELECT * FROM LogEntries
            WHERE SessionId = @SessionId
            ORDER BY OffsetSeconds
        """;

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync(sql, new { SessionId = sessionId });
        return rows.Select(MapEntry).ToList();
    }

    public async Task<IReadOnlyList<LogEntry>> GetEntriesByTypeAsync(
        int sessionId, string eventType)
    {
        await FlushAsync();

        const string sql = """
            SELECT * FROM LogEntries
            WHERE SessionId = @SessionId AND EventType = @EventType
            ORDER BY OffsetSeconds
        """;

        await using var conn = CreateConnection();
        var rows = await conn.QueryAsync(sql, new { SessionId = sessionId, EventType = eventType });
        return rows.Select(MapEntry).ToList();
    }

    public void AdjustSessionStart(int sessionId, DateTime realStart)
    {
        _sessionStarts[sessionId] = realStart;
    }

    public async Task UpdateSessionVideoPathAsync(int sessionId, string newVideoPath)
    {
        const string sql = """
            UPDATE RecordingSessions
            SET VideoFilePath = @VideoFilePath
            WHERE Id = @Id
        """;

        await using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            VideoFilePath = newVideoPath,
            Id            = sessionId
        });
    }

    private async Task FlushAsync()
    {
        if (_disposed && _pending.IsEmpty) return;
        if (_pending.IsEmpty) return;

        if (!await _flushGate.WaitAsync(0)) return;

        try
        {
            const int MaxBatch = 150;
            var batch = new List<PendingEvent>(MaxBatch);

            while (batch.Count < MaxBatch && _pending.TryDequeue(out var ev))
                batch.Add(ev);

            if (batch.Count == 0) return;

            var sqlParts = new System.Text.StringBuilder(
                "INSERT INTO LogEntries " +
                "(SessionId, Timestamp, OffsetSeconds, EventType, Description, Metadata) VALUES ");

            var parameters = new DynamicParameters();

            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sqlParts.Append(',');
                sqlParts.Append(
                    $"(@sid{i},@ts{i},@off{i},@et{i},@desc{i},@meta{i})");

                var ev = batch[i];
                parameters.Add($"sid{i}",  ev.SessionId);
                parameters.Add($"ts{i}",   ev.Timestamp.ToString("O"));
                parameters.Add($"off{i}",  ev.OffsetSeconds);
                parameters.Add($"et{i}",   ev.EventType);
                parameters.Add($"desc{i}", ev.Description);
                parameters.Add($"meta{i}", ev.Metadata);
            }

            await using var conn = CreateConnection();
            await conn.ExecuteAsync(sqlParts.ToString(), parameters);

            Log.Debug("LogService: flushed {Count} events to DB", batch.Count);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "LogService: FlushAsync failed");
        }
        finally
        {
            if (!_disposed)
                _flushGate.Release();
        }
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        DatabaseInitializer.ApplyPragmas(conn);
        return conn;
    }

    private static RecordingSession MapSession(dynamic r) => new()
    {
        Id            = (int)r.Id,
        Name          = (string)r.Name,
        StartTime     = DateTime.Parse((string)r.StartTime),
        EndTime       = r.EndTime is null ? null : DateTime.Parse((string)r.EndTime),
        Mode          = (string)r.Mode,
        VideoFilePath = (string)r.VideoFilePath
    };

    private static LogEntry MapEntry(dynamic r) => new()
    {
        Id          = (int)r.Id,
        SessionId   = (int)r.SessionId,
        Timestamp   = DateTime.Parse((string)r.Timestamp),
        Offset      = TimeSpan.FromSeconds((double)r.OffsetSeconds),
        EventType   = (string)r.EventType,
        Description = (string)r.Description,
        Metadata    = (string?)r.Metadata
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _flushTimer.DisposeAsync();
        await FlushAsync();
        _flushGate.Dispose();
    }
}