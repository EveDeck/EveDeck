using System.Collections.ObjectModel;
using EveDeck.Services;
using EveDeck.Utilities;

namespace EveDeck.ViewModels;

// Drives the "What's New" dialog. One window serves two contexts: shown after an update (you're now
// running vX, here's what changed) and shown when an update becomes available (vX is out, here's
// what's in it) -- AvailableUpdateVersion switches the header/footer between them.
public sealed class ChangelogViewModel : ObservableObject
{
    private readonly ChangelogService _service;

    public ObservableCollection<ChangelogEntryViewModel> Releases { get; } = new();

    private bool _isLoading = true;
    public bool IsLoading
    {
        get => _isLoading;
        private set { if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(ShowReleases)); }
    }

    private bool _hasError;
    public bool HasError
    {
        get => _hasError;
        private set { if (SetProperty(ref _hasError, value)) OnPropertyChanged(nameof(ShowReleases)); }
    }

    public bool ShowReleases => !IsLoading && !HasError;

    public string? AvailableUpdateVersion { get; }
    public bool IsUpdateAvailable => !string.IsNullOrEmpty(AvailableUpdateVersion);
    public string HeaderText => IsUpdateAvailable
        ? $"EveDeck {AvailableUpdateVersion} is available"
        : "What's new in EveDeck";
    public string SubText => IsUpdateAvailable
        ? "Here's what's in the new version, plus recent history:"
        : "Here's what changed recently:";
    public string UpdateButtonText => $"Update to v{AvailableUpdateVersion}";

    public ChangelogViewModel(string? availableUpdateVersion, LogService? log)
    {
        AvailableUpdateVersion = availableUpdateVersion;
        _service = new ChangelogService(log);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var notes = await _service.FetchRecentReleasesAsync(3);
        IsLoading = false;
        if (notes.Count == 0) { HasError = true; return; }
        foreach (var note in notes) Releases.Add(new ChangelogEntryViewModel(note));
    }
}

// One release tile. Clicking it expands the notes in place rather than opening a browser -- the
// changelog is the thing the user asked to see, so making them leave the app to read the rest of it
// was the wrong trade. Holds the expand state, which is why the tiles are wrapped in a view-model
// instead of binding the immutable ReleaseNote record straight from the service.
public sealed class ChangelogEntryViewModel : ObservableObject
{
    private readonly ChangelogService.ReleaseNote _note;

    public ChangelogEntryViewModel(ChangelogService.ReleaseNote note)
    {
        _note = note;
        // No CanExecute guard: a disabled Button dims its whole content, so a release short enough
        // to need no expanding would render greyed out. It stays enabled and simply does nothing.
        ToggleCommand = new RelayCommand(() => { if (CanExpand) IsExpanded = !IsExpanded; });
    }

    public string Version => _note.Version;
    public string HtmlUrl => _note.HtmlUrl;
    public bool CanExpand => _note.IsTruncated;
    public RelayCommand ToggleCommand { get; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(BodyText));
            OnPropertyChanged(nameof(ToggleText));
        }
    }

    public string BodyText => IsExpanded ? _note.Full : _note.Summary;
    public string ToggleText => IsExpanded ? "Show less ▴" : "Show more ▾";
}
