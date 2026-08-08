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

    public ObservableCollection<ChangelogService.ReleaseNote> Releases { get; } = new();

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
        foreach (var note in notes) Releases.Add(note);
    }
}
