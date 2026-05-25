using Dapper;
using Microsoft.Data.Sqlite;

namespace Diploma.Data.Migrations;

public sealed class Migration_002_AppSettings : IMigration
{
    public int TargetVersion => 2;

    public void Apply(SqliteConnection conn)
    {
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS AppSettings (
                Key   TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL
            );
        """);
    }
}
