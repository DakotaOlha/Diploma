using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Diploma.Core.Services;

public class LogService : ILogService
{
    private readonly string _connectionString;

    private readonly Dictionary<int, DateTime> _sessionStarts = new();

    public LogService(string connectionString)
    {
        _connectionString = connectionString;
    }
    
    public async Task<int> StartSessionAsync(string name, RecordingMode mode, string videoFilePath)
    {
        var now = DateTime.UtcNow;

        const string sql = """
            INSERT INTO RecordingSessions (Name, StartTime, Mode, VideoFilePath)
            VALUES (@Name, @StartTime, @Mode, @VideoFilePath);
            SELECT last_insert_rowid();
        """;

        using var conn = CreateConnection();
        
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
        const string sql = """
                               UPDATE RecordingSessions
                               SET EndTime = @EndTime
                               WHERE Id = @Id
                           """;
        
        using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            EndTime = DateTime.UtcNow.ToString("O"),
            Id      = sessionId
        });
        
        _sessionStarts.Remove(sessionId);
    }
    
    public async Task DeleteSessionAsync(int sessionId)
    {
        const string sql = "DELETE FROM RecordingSessions WHERE Id = @Id";
     
        using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new { Id = sessionId });
     
        _sessionStarts.Remove(sessionId);
    }

    public async Task<RecordingSession?> GetSessionAsync(int sessionId)
    {
        const string sql = "SELECT * FROM RecordingSessions WHERE Id = @Id";

        using var conn = CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync(sql, new { Id = sessionId });
        return row is null ? null : MapSession(row);
    }

    public async Task<IReadOnlyList<RecordingSession>> GetAllSessionsAsync()
    {
        const string sql = "SELECT * FROM RecordingSessions ORDER BY StartTime DESC";

        using var conn = CreateConnection();
        var rows = await conn.QueryAsync(sql);
        return rows.Select(MapSession).ToList();
    }

    public async Task LogEventAsync(int sessionId, string eventType, string description, string? metadata = null)
    {
        var now = DateTime.UtcNow;

        var offset = _sessionStarts.TryGetValue(sessionId, out var start)
            ? (now - start).TotalSeconds
            : 0.0;

        const string sql = """
                               INSERT INTO LogEntries
                                   (SessionId, Timestamp, OffsetSeconds, EventType, Description, Metadata)
                               VALUES
                                   (@SessionId, @Timestamp, @OffsetSeconds, @EventType, @Description, @Metadata)
                           """;

        using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            SessionId     = sessionId,
            Timestamp     = now.ToString("O"),
            OffsetSeconds = offset,
            EventType     = eventType,
            Description   = description,
            Metadata      = metadata
        });
    }

    public async Task<IReadOnlyList<LogEntry>> GetEntriesAsync(int sessionId)
    {
        const string sql = """
                               SELECT * FROM LogEntries
                               WHERE SessionId = @SessionId
                               ORDER BY OffsetSeconds
                           """;

        using var conn = CreateConnection();
        var rows = await conn.QueryAsync(sql, new { SessionId = sessionId });
        return rows.Select(MapEntry).ToList();
    }

    public async Task<IReadOnlyList<LogEntry>> GetEntriesByTypeAsync(int sessionId, string eventType)
    {
        const string sql = """
                               SELECT * FROM LogEntries
                               WHERE SessionId = @SessionId AND EventType = @EventType
                               ORDER BY OffsetSeconds
                           """;

        using var conn = CreateConnection();
        var rows = await conn.QueryAsync(sql, new { SessionId = sessionId, EventType = eventType });
        return rows.Select(MapEntry).ToList();
    }
    
    public void AdjustSessionStart(int sessionId, DateTime realStart)
    {
        _sessionStarts[sessionId] = realStart;
    }
    
    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
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
    
    public async Task UpdateSessionVideoPathAsync(int sessionId, string newVideoPath)
    {
        const string sql = """
                               UPDATE RecordingSessions
                               SET VideoFilePath = @VideoFilePath
                               WHERE Id = @Id
                           """;

        using var conn = CreateConnection();
        await conn.ExecuteAsync(sql, new
        {
            VideoFilePath = newVideoPath,
            Id            = sessionId
        });
    }
}