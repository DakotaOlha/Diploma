using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Diploma.Data.Database;

public class DatabaseInitializer
{
    private readonly string _connectionString;
    
    public DatabaseInitializer(string connectionString)
    {
        _connectionString = connectionString;
    }

    public void Initialize()
    {
        var dir = Path.GetDirectoryName(
            new SqliteConnectionStringBuilder(_connectionString).DataSource);
        
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        
        conn.Execute("""
        CREATE TABLE IF NOT EXISTS RecordingSessions (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            Name          TEXT    NOT NULL,
            StartTime     TEXT    NOT NULL,
            EndTime       TEXT,
            Mode          TEXT    NOT NULL DEFAULT 'Personal',
            VideoFilePath TEXT    NOT NULL DEFAULT ''
        );
        
        CREATE TABLE IF NOT EXISTS LogEntries (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            SessionId     INTEGER NOT NULL,
            Timestamp     TEXT    NOT NULL,
            OffsetSeconds REAL    NOT NULL DEFAULT 0,
            EventType     TEXT    NOT NULL,
            Description   TEXT    NOT NULL,
            Metadata      TEXT,
            FOREIGN KEY (SessionId) REFERENCES RecordingSessions(Id) ON DELETE CASCADE
        );
        
        CREATE INDEX IF NOT EXISTS idx_log_session
            ON LogEntries(SessionId);
        
        CREATE INDEX IF NOT EXISTS idx_log_event_type
            ON LogEntries(SessionId, EventType);
        """);
    }
}