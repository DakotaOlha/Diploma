using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using Diploma.Core.Services;
using Diploma.Views;

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
        if (SelectedSession is null) 
            return;
        
        string sanitizedName = SelectedSession.Name.Replace(" ", "_").Replace(":", "-");
        
        var path = PickSavePath("JSON файл|*.json", $"{sanitizedName}.json");
        if (path is null) return;
        await _exportService.ExportJsonAsync(SelectedSession.Id, path);
    }

    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        if (SelectedSession is null) 
            return;
        
        string sanitizedName = SelectedSession.Name.Replace(" ", "_").Replace(":", "-");
        
        var path = PickSavePath("Markdown файл|*.md", $"{sanitizedName}.md");
        if (path is null) return;
        await _exportService.ExportMarkdownAsync(SelectedSession.Id, path);
    }

    [RelayCommand]
    private async Task ExportYouTubeChaptersAsync()
    {
        if (SelectedSession is null) 
            return;
        
        string sanitizedName = SelectedSession.Name.Replace(" ", "_").Replace(":", "-");
        
        var path = PickSavePath("Text файл|*.txt", $"{sanitizedName}_chapters.txt");
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
    private async Task RenameSessionAsync(RecordingSession? session)
    {
        if (session is null) return;

        var dialog = new RenameDialog(session.Name);
        if (dialog.ShowDialog() != true) return;

        var savedId = session.Id;
        await _logService.UpdateSessionNameAsync(savedId, dialog.NewName);

        // Оновлюємо список в пам'яті без зміни SelectedSession
        var allSessions = await _logService.GetAllSessionsAsync();

        var currentSelectedId = SelectedSession?.Id;
        Sessions.Clear();
        foreach (var s in allSessions)
            Sessions.Add(s);

        // Відновлюємо вибір на перейменовану сесію (або на попередньо вибрану)
        var updated = Sessions.FirstOrDefault(s => s.Id == savedId)
                   ?? Sessions.FirstOrDefault(s => s.Id == currentSelectedId);

        SelectedSession = updated ?? Sessions.FirstOrDefault();
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(RecordingSession? session)
    {
        if (session is null) return;

        var result = MessageBox.Show(
            $"Видалити сесію «{session.Name}» та всі пов'язані відеофайли?\n\nЦю дію неможливо скасувати.",
            "Підтвердження видалення",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        
        if (result != MessageBoxResult.Yes)
            return;
        
        string? sessionDirectory = ResolveSessionDirectory(session);
        
        if (sessionDirectory is not null && IsDirectoryShared(sessionDirectory, session.Id))
        {
            sessionDirectory = null;
        }
        
        await _logService.DeleteSessionAsync(session.Id);
        Sessions.Remove(session);

        if (SelectedSession?.Id == session.Id)
        {
            SelectedSession = Sessions.FirstOrDefault();
            Entries.Clear();
        }

        if (sessionDirectory is not null)
        {
            await DeleteSessionDirectoryAsync(sessionDirectory, session.Id);
        }
    }
    
    private static string? ResolveSessionDirectory(RecordingSession session)
    {
        if (string.IsNullOrWhiteSpace(session.VideoFilePath))
            return null;

        try
        {
            string? dir = Path.GetDirectoryName(
                Path.GetFullPath(session.VideoFilePath));

            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SessionsViewModel] Invalid VideoFilePath for session {session.Id}: {ex.Message}");
            return null;
        }
    }
    
    private bool IsDirectoryShared(string directory, int excludeSessionId)
    {
        return Sessions
            .Where(s => s.Id != excludeSessionId)
            .Select(s => ResolveSessionDirectory(s))
            .Any(d => d is not null &&
                      string.Equals(d, directory, StringComparison.OrdinalIgnoreCase));
    }
    
    private static async Task DeleteSessionDirectoryAsync(string directory, int sessionId)
    {
        await Task.Yield();

        if (!Directory.Exists(directory))
            return;

        const int maxAttempts = 3;
        const int retryDelayMs = 500;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return; 
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SessionsViewModel] Access denied deleting session {sessionId} " +
                    $"directory (attempt {attempt}/{maxAttempts}): {ex.Message}");
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SessionsViewModel] IO error deleting session {sessionId} " +
                    $"directory (attempt {attempt}/{maxAttempts}): {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SessionsViewModel] Unexpected error deleting session {sessionId} " +
                    $"directory: {ex}");
                return;
            }

            if (attempt < maxAttempts)
                await Task.Delay(retryDelayMs);
        }

        System.Diagnostics.Debug.WriteLine(
            $"[SessionsViewModel] Could not delete directory for session {sessionId} " +
            $"after {maxAttempts} attempts. Manual cleanup may be required: {directory}");
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