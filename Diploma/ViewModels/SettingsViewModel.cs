using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diploma.Core.Interfaces;
using Diploma.Core.Models;
using Diploma.Core.Services;

namespace Diploma.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService   _settings;
    private readonly ModeProfileService _profiles;

    public IReadOnlyList<CaptureQualityProfile> AvailableQualities { get; } =
    [
        CaptureQualityProfile.Low,
        CaptureQualityProfile.Medium,
        CaptureQualityProfile.High,
    ];

    public IReadOnlyList<ModeProfile> AvailableModes { get; }

    [ObservableProperty] private CaptureQualityProfile? _defaultQuality;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsOlympicDefault), nameof(IsLearningDefault),
        nameof(IsPersonalDefault), nameof(IsWorkDefault))]
    private ModeProfile? _defaultMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsGeneralSection), nameof(IsLoggingSection),
        nameof(IsHotkeysSection), nameof(SectionTitle))]
    private int _selectedSection;

    public bool   IsGeneralSection  => SelectedSection == 0;
    public bool   IsLoggingSection  => SelectedSection == 1;
    public bool   IsHotkeysSection  => SelectedSection == 2;
    public string SectionTitle      => SelectedSection switch
    {
        0 => "Загальні",
        1 => "Логування",
        _ => "Гарячі клавіші"
    };

    public bool IsOlympicDefault  => DefaultMode?.Mode == RecordingMode.Olympic;
    public bool IsLearningDefault => DefaultMode?.Mode == RecordingMode.Learning;
    public bool IsPersonalDefault => DefaultMode?.Mode == RecordingMode.Personal;
    public bool IsWorkDefault     => DefaultMode?.Mode == RecordingMode.Work;

    [RelayCommand]
    private void SelectSection(string idx)
    {
        if (int.TryParse(idx, out var i)) SelectedSection = i;
    }

    // Learning events
    [ObservableProperty] private bool _learningClipboard;
    [ObservableProperty] private bool _learningFileSave;
    [ObservableProperty] private bool _learningUndo;
    [ObservableProperty] private bool _learningRunDebug;
    [ObservableProperty] private bool _learningIdle;

    // Work events
    [ObservableProperty] private bool _workClipboard;
    [ObservableProperty] private bool _workFileSave;
    [ObservableProperty] private bool _workRunDebug;
    [ObservableProperty] private bool _workIdle;

    // Personal events
    [ObservableProperty] private bool _personalClipboard;
    [ObservableProperty] private bool _personalFileSave;
    [ObservableProperty] private bool _personalRunDebug;
    [ObservableProperty] private bool _personalIdle;
    [ObservableProperty] private bool _personalIde;
    [ObservableProperty] private bool _personalFileSwitch;
    [ObservableProperty] private bool _personalAutoSave;

    [ObservableProperty] private string _saveStatus = string.Empty;

    public SettingsViewModel(ISettingsService settingsService, ModeProfileService profileService)
    {
        _settings = settingsService;
        _profiles = profileService;
        AvailableModes = _profiles.GetAllProfiles();
        LoadFromCurrent();
    }

    public void Reload() => LoadFromCurrent();

    private void LoadFromCurrent()
    {
        var s = _settings.Current;

        DefaultQuality = AvailableQualities.FirstOrDefault(q => q.Quality == s.DefaultQuality)
                         ?? CaptureQualityProfile.Medium;
        DefaultMode    = AvailableModes.FirstOrDefault(m => m.Mode == s.DefaultMode)
                         ?? AvailableModes.First(m => m.Mode == RecordingMode.Personal);

        LearningClipboard = s.LearningEvents.Contains(EventTypes.ClipboardCopy);
        LearningFileSave  = s.LearningEvents.Contains(EventTypes.FileSave);
        LearningUndo      = s.LearningEvents.Contains(EventTypes.Undo);
        LearningRunDebug  = s.LearningEvents.Contains(EventTypes.RunOrDebug);
        LearningIdle      = s.LearningEvents.Contains(EventTypes.IdleStart);

        WorkClipboard = s.WorkEvents.Contains(EventTypes.ClipboardCopy);
        WorkFileSave  = s.WorkEvents.Contains(EventTypes.FileSave);
        WorkRunDebug  = s.WorkEvents.Contains(EventTypes.RunOrDebug);
        WorkIdle      = s.WorkEvents.Contains(EventTypes.IdleStart);

        PersonalClipboard  = s.PersonalEvents.Contains(EventTypes.ClipboardCopy);
        PersonalFileSave   = s.PersonalEvents.Contains(EventTypes.FileSave);
        PersonalRunDebug   = s.PersonalEvents.Contains(EventTypes.RunOrDebug);
        PersonalIdle       = s.PersonalEvents.Contains(EventTypes.IdleStart);
        PersonalIde        = s.PersonalEvents.Contains(EventTypes.IdeOpened);
        PersonalFileSwitch = s.PersonalEvents.Contains(EventTypes.FileSwitched);
        PersonalAutoSave   = s.PersonalEvents.Contains(EventTypes.FileSavedAuto);
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var s = _settings.Current;

        s.DefaultQuality = DefaultQuality?.Quality ?? CaptureQuality.Medium;
        s.DefaultMode    = DefaultMode?.Mode ?? RecordingMode.Personal;

        s.LearningEvents = BuildSet(
            (EventTypes.ClipboardCopy,  LearningClipboard),
            (EventTypes.ClipboardPaste, LearningClipboard),
            (EventTypes.FileSave,       LearningFileSave),
            (EventTypes.Undo,           LearningUndo),
            (EventTypes.RunOrDebug,     LearningRunDebug),
            (EventTypes.IdleStart,      LearningIdle),
            (EventTypes.IdleEnd,        LearningIdle),
            (EventTypes.ManualMarker,   true),
            (EventTypes.Screenshot,     true));

        s.WorkEvents = BuildSet(
            (EventTypes.ClipboardCopy,  WorkClipboard),
            (EventTypes.ClipboardPaste, WorkClipboard),
            (EventTypes.FileSave,       WorkFileSave),
            (EventTypes.RunOrDebug,     WorkRunDebug),
            (EventTypes.IdleStart,      WorkIdle),
            (EventTypes.IdleEnd,        WorkIdle),
            (EventTypes.ManualMarker,   true),
            (EventTypes.Screenshot,     true));

        s.PersonalEvents = BuildSet(
            (EventTypes.ClipboardCopy,  PersonalClipboard),
            (EventTypes.ClipboardPaste, PersonalClipboard),
            (EventTypes.FileSave,       PersonalFileSave),
            (EventTypes.RunOrDebug,     PersonalRunDebug),
            (EventTypes.IdleStart,      PersonalIdle),
            (EventTypes.IdleEnd,        PersonalIdle),
            (EventTypes.IdeOpened,      PersonalIde),
            (EventTypes.IdeClosed,      PersonalIde),
            (EventTypes.FileSwitched,   PersonalFileSwitch),
            (EventTypes.FileSavedAuto,  PersonalAutoSave),
            (EventTypes.ManualMarker,   true),
            (EventTypes.Screenshot,     true));

        await _settings.SaveAsync();
        _settings.ApplyToProfileService(_profiles);

        SaveStatus = "✓ Налаштування збережено";
        await Task.Delay(2_500);
        SaveStatus = string.Empty;
    }

    private static HashSet<string> BuildSet(params (string Type, bool On)[] pairs) =>
        pairs.Where(p => p.On).Select(p => p.Type).ToHashSet();
}
