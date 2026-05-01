using System.IO;
using Microsoft.Data.Sqlite;
using Diploma.Data.Migrations;
using Serilog;

namespace Diploma.Data.Database;

public class DatabaseInitializer
{
    private readonly string _connectionString;

    private static readonly IMigration[] Migrations =
    [
        new Migration_001_InitialSchema(),
    ];

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

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
        }

        var currentVersion = GetUserVersion(conn);
        Log.Information("DatabaseInitializer: current schema version = {Version}", currentVersion);

        var pending = Migrations
            .Where(m => m.TargetVersion > currentVersion)
            .OrderBy(m => m.TargetVersion)
            .ToList();

        if (pending.Count == 0)
        {
            Log.Information("DatabaseInitializer: schema is up to date.");
            return;
        }

        foreach (var migration in pending)
        {
            Log.Information("DatabaseInitializer: applying migration → v{Version}", 
                migration.TargetVersion);

            using var tx = conn.BeginTransaction();
            try
            {
                migration.Apply(conn);
                SetUserVersion(conn, migration.TargetVersion);
                tx.Commit();

                Log.Information("DatabaseInitializer: migration v{Version} applied.", 
                    migration.TargetVersion);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                Log.Fatal(ex, "DatabaseInitializer: migration v{Version} FAILED — rolled back.",
                    migration.TargetVersion);
                throw;
            }
        }
    }

    private static int GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection conn, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }
}