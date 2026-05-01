namespace Diploma.Data.Migrations;

public interface IMigration
{
    int TargetVersion { get; }
    void Apply(Microsoft.Data.Sqlite.SqliteConnection conn);
}