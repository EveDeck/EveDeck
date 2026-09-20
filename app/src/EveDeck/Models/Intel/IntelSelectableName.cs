using EveDeck.Utilities;

namespace EveDeck.Models.Intel;

/// <summary>
/// A tickable name in the intel settings — one discovered channel, or one character available to
/// follow. The ViewModel supplies <see cref="SelectionChanged"/> so a tick writes straight through to
/// the saved collection rather than needing a separate apply step.
/// </summary>
public sealed class IntelSelectableName : ObservableObject
{
    private bool _isSelected;

    public IntelSelectableName(string name, bool isSelected, Action<IntelSelectableName>? selectionChanged = null)
    {
        Name = name;
        _isSelected = isSelected;
        SelectionChanged = selectionChanged;
    }

    public string Name { get; }

    public Action<IntelSelectableName>? SelectionChanged { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            SelectionChanged?.Invoke(this);
        }
    }
}
