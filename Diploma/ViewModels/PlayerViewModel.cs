using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using LibVLCSharp.Shared;
using System.Collections.ObjectModel;
using System.IO;

namespace Diploma.ViewModels;

public partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly LibVLC _libVlc;
    public MediaPlayer MediaPlayer { get; }

    private readonly ILogService _logService;
    private bool _disposed;

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _hasMedia;
    [ObservableProperty] private long _durationMs;   
    [ObservableProperty] private long _positionMs;      
    [ObservableProperty] private float _volume = 100;

    [ObservableProperty] private string _timeLabel = "00:00:00 / 00:00:00";

    public ObservableCollection<LogEntry> Entries { get; } = new();

    [ObservableProperty] private LogEntry? _activeEntry;

    [ObservableProperty] private RecordingSession? _currentSession;

    public PlayerViewModel(ILogService logService)
    {
        _logService = logService;

        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC(enableDebugLogs: false);
        MediaPlayer = new MediaPlayer(_libVlc);

        SubscribeToPlayerEvents();
    }

    public async Task OpenSessionAsync(RecordingSession session)
    {
        CurrentSession = session;

        var entries = await _logService.GetEntriesAsync(session.Id);
        Entries.Clear();
        foreach (var e in entries)
            Entries.Add(e);

        await Task.Run(() => MediaPlayer.Stop());

        if (!File.Exists(session.VideoFilePath))
        {
            HasMedia = false;
            return;
        }

        var oldMedia = MediaPlayer.Media;

        var media = new Media(_libVlc, session.VideoFilePath, FromType.FromPath);
        await media.Parse(MediaParseOptions.ParseLocal);

        DurationMs = media.Duration;
        UpdateTimeLabel(0, DurationMs);

        MediaPlayer.Media = media;
    
        oldMedia?.Dispose();

        HasMedia = true;
    }

    public void JumpTo(TimeSpan offset)
    {
        if (!HasMedia) return;

        var ms = (long)offset.TotalMilliseconds;

        if (MediaPlayer.IsPlaying)
        {
            MediaPlayer.Time = ms;
            return;
        }

        var tcs = new TaskCompletionSource<bool>();

        void OnPlaying(object? s, EventArgs e)
        {
            MediaPlayer.Playing -= OnPlaying;
            tcs.TrySetResult(true);
        }

        void OnError(object? s, EventArgs e)
        {
            MediaPlayer.EncounteredError -= OnError;
            tcs.TrySetResult(false);
        }

        MediaPlayer.Playing += OnPlaying;
        MediaPlayer.EncounteredError += OnError;
        MediaPlayer.Play();

        Task.Run(async () =>
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000));

            if (completed == tcs.Task && tcs.Task.Result)
            {
                await Task.Delay(200);
                MediaPlayer.Time = ms;
                await Task.Delay(100);
                MediaPlayer.Pause();
            }
        });
    }
    
    [RelayCommand]
    private void JumpToEntry(LogEntry? entry)
    {
        if (entry is null) return;
        JumpTo(entry.Offset);
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (!HasMedia) return;

        if (MediaPlayer.IsPlaying)
            MediaPlayer.Pause();
        else
            MediaPlayer.Play();
    }

    [RelayCommand]
    private void Stop()
    {
        MediaPlayer.Stop();
        PositionMs = 0;
        UpdateTimeLabel(0, DurationMs);
    }

    [RelayCommand]
    private void Seek(long ms)
    {
        if (HasMedia)
            MediaPlayer.Time = ms;
    }

    private void SubscribeToPlayerEvents()
    {
        MediaPlayer.Playing += (_, _) =>
            Dispatch(() => IsPlaying = true);

        MediaPlayer.Paused += (_, _) =>
            Dispatch(() => IsPlaying = false);

        MediaPlayer.Stopped += (_, _) =>
            Dispatch(() =>
            {
                IsPlaying = false;
                PositionMs = 0;
            });

        MediaPlayer.TimeChanged += (_, e) =>
            Dispatch(() =>
            {
                PositionMs = e.Time;
                UpdateTimeLabel(e.Time, DurationMs);
                UpdateActiveEntry(e.Time);
            });

        MediaPlayer.LengthChanged += (_, e) =>
            Dispatch(() =>
            {
                DurationMs = e.Length;
                UpdateTimeLabel(PositionMs, e.Length);
            });
    }

    private void UpdateActiveEntry(long currentMs)
    {
        if (Entries.Count == 0) return;

        var currentOffset = TimeSpan.FromMilliseconds(currentMs);

        LogEntry? match = null;
        foreach (var entry in Entries)
        {
            if (entry.Offset <= currentOffset)
                match = entry;
            else
                break;
        }

        if (match != ActiveEntry)
            ActiveEntry = match;
    }

    private void UpdateTimeLabel(long currentMs, long totalMs)
    {
        var current = TimeSpan.FromMilliseconds(currentMs);
        var total = TimeSpan.FromMilliseconds(totalMs > 0 ? totalMs : 0);
        TimeLabel = $"{current:hh\\:mm\\:ss} / {total:hh\\:mm\\:ss}";
    }

    private static void Dispatch(Action action) =>
        System.Windows.Application.Current.Dispatcher.Invoke(action);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        MediaPlayer.Stop();
        MediaPlayer.Dispose();
        _libVlc.Dispose();

        GC.SuppressFinalize(this);
    }
}