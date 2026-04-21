using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;

namespace Diploma.ViewModels;

public partial class SessionsViewModel : ObservableObject
{
    private readonly ILogService _logService;
    private readonly PlayerViewModel _playerViewModel;
    
    public bool HasSession => SelectedSession is not null;
    public bool HasNoSession => SelectedSession is null;

    public ObservableCollection<RecordingSession> Sessions { get; } = new();
    public ObservableCollection<LogEntry> Entries { get; } = new();

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

    public SessionsViewModel(ILogService logService, PlayerViewModel playerViewModel)
    {
        _logService = logService;
        _playerViewModel = playerViewModel;
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

    public event EventHandler<TimeSpan>? JumpToRequested;

    [RelayCommand]
    private void JumpToEntry(LogEntry? entry)
    {
        if (entry is null) return;
        _playerViewModel.JumpTo(entry.Offset);
    }
}