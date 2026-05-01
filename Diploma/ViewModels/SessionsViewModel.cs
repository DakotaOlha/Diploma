using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using Diploma.Core.Services;

namespace Diploma.ViewModels;

public partial class SessionsViewModel : ObservableObject
{
    private readonly ILogService _logService;
    private readonly PlayerViewModel _playerViewModel;
    
    public bool HasSession => SelectedSession is not null;
    public bool HasNoSession => SelectedSession is null;

    public ObservableCollection<RecordingSession> Sessions { get; } = new();
    public ObservableCollection<LogEntry> Entries { get; } = new();
    
    private readonly ExportService _exportService;

    [ObservableProperty]
    private RecordingSession? _selectedSession;

    [ObservableProperty]
    private LogEntry? _selectedEntry;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    partial void OnSelectedSessionChanged(RecordingSession? value)
    {
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(HasNoSession));
 
        if (value is not null)
        {
            _ = LoadEntriesAsync(value.Id);
 
            _ = _playerViewModel.OpenSessionAsync(value);
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public SessionsViewModel(ILogService logService, PlayerViewModel playerViewModel, ExportService exportService)
    {
        _logService      = logService;
        _playerViewModel = playerViewModel;
        _exportService   = exportService;
    }

    [RelayCommand]
    public async Task LoadSessionsAsync()
    {
        IsLoading = true;
        try
        {
            var sessions = await _logService.GetAllSessionsAsync();

            Sessions.Clear();
            foreach (var s in sessions)
                Sessions.Add(s);

            if (Sessions.Count > 0)
                SelectedSession = Sessions[0];
        }
        finally
        {
            IsLoading = false;
        }
    }
    
    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        if (SelectedSession is null) return;
        var path = PickSavePath("JSON файл|*.json", $"{SelectedSession.Name}.json");
        if (path is null) return;
        await _exportService.ExportJsonAsync(SelectedSession.Id, path);
    }

    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        if (SelectedSession is null) return;
        var path = PickSavePath("Markdown файл|*.md", $"{SelectedSession.Name}.md");
        if (path is null) return;
        await _exportService.ExportMarkdownAsync(SelectedSession.Id, path);
    }

    [RelayCommand]
    private async Task ExportYouTubeChaptersAsync()
    {
        if (SelectedSession is null) return;
        var path = PickSavePath("Text файл|*.txt", $"{SelectedSession.Name}_chapters.txt");
        if (path is null) return;
        await _exportService.ExportYouTubeChaptersAsync(SelectedSession.Id, path);
    }
    
    private static string? PickSavePath(string filter, string defaultName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter   = filter,
            FileName = defaultName
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private async Task LoadEntriesAsync(int sessionId)
    {
        IsLoading = true;
        try
        {
            var entries = await _logService.GetEntriesAsync(sessionId);
            _allEntries = entries.ToList();
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private List<LogEntry> _allEntries = new();

    private void ApplyFilter()
    {
        Entries.Clear();

        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? _allEntries
            : _allEntries.Where(e =>
                e.EventType.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                e.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        foreach (var e in filtered)
            Entries.Add(e);
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(RecordingSession? session)
    {
        if (session is null) return;

        await _logService.DeleteSessionAsync(session.Id);
        Sessions.Remove(session);

        if (SelectedSession == session)
        {
            SelectedSession = Sessions.FirstOrDefault();
            Entries.Clear();
        }
    }

    [RelayCommand]
    private async void JumpToEntry(LogEntry? entry)
    {
        if (entry is null) return;
    
        if (_playerViewModel.CurrentSession?.Id != SelectedSession?.Id 
            && SelectedSession is not null)
        {
            await _playerViewModel.OpenSessionAsync(SelectedSession);
        }
    
        _playerViewModel.JumpTo(entry.Offset);
        NavigateToPlayer?.Invoke();
    }
    
    public event Action? NavigateToPlayer;
}