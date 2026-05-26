using Dapper;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Diploma.Core.Services;

public class AppSettingsService : ISettingsService
{
    private readonly string _connStr;
    private AppSettings _current = new();

    public AppSettings Current => _current;

    public AppSettingsService(string connectionString) => _connStr = connectionString;

    public async Task LoadAsync()
    {
        try
        {
            await using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();

            var rows = await conn.QueryAsync<(string Key, string Value)>(
                "SELECT Key, Value FROM AppSettings");

            var dict = rows.ToDictionary(r => r.Key, r => r.Value);
            var s    = new AppSettings();

            if (dict.TryGetValue("DefaultQuality", out var q) &&
                Enum.TryParse<CaptureQuality>(q, out var quality))
                s.DefaultQuality = quality;

            if (dict.TryGetValue("DefaultMode", out var m) &&
                Enum.TryParse<RecordingMode>(m, out var mode))
                s.DefaultMode = mode;

            if (dict.TryGetValue("Events_Learning", out var le) && le.Length > 0)
                s.LearningEvents = ParseEvents(le);

            if (dict.TryGetValue("Events_Work", out var we) && we.Length > 0)
                s.WorkEvents = ParseEvents(we);

            if (dict.TryGetValue("Events_Personal", out var pe) && pe.Length > 0)
                s.PersonalEvents = ParseEvents(pe);

            _current = s;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AppSettingsService: failed to load settings, using defaults");
            _current = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            await using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();

            var pairs = new Dictionary<string, string>
            {
                ["DefaultQuality"]  = _current.DefaultQuality.ToString(),
                ["DefaultMode"]     = _current.DefaultMode.ToString(),
                ["Events_Learning"] = string.Join(",", _current.LearningEvents),
                ["Events_Work"]     = string.Join(",", _current.WorkEvents),
                ["Events_Personal"] = string.Join(",", _current.PersonalEvents),
            };

            foreach (var (key, value) in pairs)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO AppSettings(Key, Value) VALUES(@Key, @Value) " +
                    "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
                    new { Key = key, Value = value });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AppSettingsService: failed to save settings");
            throw;
        }
    }

    public void ApplyToProfileService(ModeProfileService profileService)
    {
        ApplyModeEvents(profileService, RecordingMode.Learning, _current.LearningEvents);
        ApplyModeEvents(profileService, RecordingMode.Work,     _current.WorkEvents);
        ApplyModeEvents(profileService, RecordingMode.Personal, _current.PersonalEvents);
    }

    private static void ApplyModeEvents(
        ModeProfileService profileService,
        RecordingMode      mode,
        HashSet<string>    events)
    {
        var src = profileService.GetProfile(mode);
        profileService.UpdateProfile(new ModeProfile
        {
            Mode           = src.Mode,
            DisplayName    = src.DisplayName,
            Description    = src.Description,
            IsCustomizable = src.IsCustomizable,
            AllowedEvents  = events,
        });
    }

    private static HashSet<string> ParseEvents(string value) =>
        [..value.Split(',', StringSplitOptions.RemoveEmptyEntries)];
}
