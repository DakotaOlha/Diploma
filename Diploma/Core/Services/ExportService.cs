using System.IO;
using System.Text;
using System.Text.Json;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;

namespace Diploma.Core.Services;

public class ExportService
{
    private readonly ILogService _logService;

    private static readonly TimeSpan ChapterMinInterval = TimeSpan.FromMinutes(2);

    public ExportService(ILogService logService)
    {
        _logService = logService;
    }

    public async Task ExportJsonAsync(int sessionId, string path)
    {
        var session = await _logService.GetSessionAsync(sessionId)
                      ?? throw new InvalidOperationException($"Session {sessionId} not found");
        var entries = await _logService.GetEntriesAsync(sessionId);

        var export = new
        {
            session = new
            {
                session.Id,
                session.Name,
                session.StartTime,
                session.EndTime,
                session.Mode,
                session.VideoFilePath
            },
            entries = entries.Select(e => new
            {
                e.Id,
                offset          = e.Offset.TotalSeconds,
                offsetFormatted = $"{e.Offset:mm\\:ss\\.f}",
                e.EventType,
                e.Description,
                e.Metadata,
                timestamp = e.Timestamp.ToString("O")
            })
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json    = JsonSerializer.Serialize(export, options);

        await File.WriteAllTextAsync(path, json, Encoding.UTF8);
    }

    public async Task ExportMarkdownAsync(int sessionId, string path)
    {
        var session = await _logService.GetSessionAsync(sessionId)
                      ?? throw new InvalidOperationException($"Session {sessionId} not found");
        var entries = await _logService.GetEntriesAsync(sessionId);

        var sb = new StringBuilder();

        sb.AppendLine($"# {session.Name}");
        sb.AppendLine();
        sb.AppendLine($"- **Режим:** {session.Mode}");
        sb.AppendLine($"- **Початок:** {session.StartTime:dd.MM.yyyy HH:mm:ss}");

        if (session.EndTime.HasValue)
        {
            var duration = session.EndTime.Value - session.StartTime;
            sb.AppendLine($"- **Кінець:** {session.EndTime.Value:dd.MM.yyyy HH:mm:ss}");
            sb.AppendLine($"- **Тривалість:** {duration:hh\\:mm\\:ss}");
        }

        sb.AppendLine($"- **Подій:** {entries.Count}");
        sb.AppendLine();
        sb.AppendLine("## Події");
        sb.AppendLine();
        sb.AppendLine("| Час | Подія | Опис | Деталі |");
        sb.AppendLine("|-----|-------|------|--------|");

        foreach (var e in entries)
        {
            var meta = string.IsNullOrWhiteSpace(e.Metadata) ? "—" : e.Metadata;
            sb.AppendLine(
                $"| `{e.Offset:mm\\:ss\\.f}` " +
                $"| {EscapeMd(e.EventType!)} " +
                $"| {EscapeMd(e.Description!)} " +
                $"| {EscapeMd(meta)} |");
        }

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
    }

    public async Task ExportYouTubeChaptersAsync(int sessionId, string path)
    {
        var entries = await _logService.GetEntriesAsync(sessionId);

        var sb = new StringBuilder();
        sb.AppendLine("00:00 Початок");

        var lastChapterOffset = new Dictionary<string, TimeSpan>();

        foreach (var e in entries)
        {
            if (!IsChapterWorthy(e.EventType!))
                continue;

            if (IsThrottled(e.EventType!, e.Offset, lastChapterOffset))
                continue;

            lastChapterOffset[e.EventType!] = e.Offset;

            sb.AppendLine($"{FormatYouTubeTimestamp(e.Offset)} {BuildChapterLabel(e)}");
        }

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
    }

    private static bool IsThrottled(
        string                      eventType,
        TimeSpan                    currentOffset,
        Dictionary<string, TimeSpan> lastChapterOffset)
    {
        if (!lastChapterOffset.TryGetValue(eventType, out var lastOffset))
            return false;

        return (currentOffset - lastOffset) < ChapterMinInterval;
    }

    private static bool IsChapterWorthy(string eventType) => eventType switch
    {
        EventTypes.ManualMarker => true,
        EventTypes.RunOrDebug   => true,
        EventTypes.IdleStart    => true,
        EventTypes.IdeOpened    => true,
        _                       => false
    };

    private static string BuildChapterLabel(LogEntry entry) => (entry.EventType switch
    {
        EventTypes.ManualMarker => entry.Description,
        EventTypes.RunOrDebug   => $"▶ {entry.Description}",
        EventTypes.IdleStart    => "⏸ Пауза",
        EventTypes.IdeOpened    => $"🖥 {entry.Description}",
        _ => entry.Description
    })!;

    private static string FormatYouTubeTimestamp(TimeSpan offset)
    {
        return offset.TotalHours >= 1
            ? $"{(int)offset.TotalHours}:{offset.Minutes:D2}:{offset.Seconds:D2}"
            : $"{offset.Minutes:D2}:{offset.Seconds:D2}";
    }

    private static string EscapeMd(string text) =>
        text.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
}