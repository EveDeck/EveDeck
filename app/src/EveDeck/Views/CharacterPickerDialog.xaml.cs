using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using EveDeck.Models;

namespace EveDeck.Views;

// "Which characters are in this set?" -- a flat, filterable checkbox list of the whole known roster.
//
// Flat rather than grouped on purpose: ESI exposes no account/owner field, so there is no reliable
// way to group a person's alts automatically, and grouping by corporation buckets a typical alt
// roster into one useless heading. A filter box plus All/None scales to any roster size, which
// matters because nothing here may assume a particular number of accounts.
public partial class CharacterPickerDialog : Window
{
    private readonly List<CharacterPickItem> _items;

    public CharacterPickerDialog(string setName, IReadOnlyList<CharacterPickItem> items)
    {
        InitializeComponent();
        _items = items.ToList();

        HeadingText.Text = $"Characters in “{setName}”";
        CharacterList.ItemsSource = _items;

        // Live count, so the consequence of the current ticks (how many seats this set will end up
        // with) is visible before Apply rather than discovered afterwards.
        foreach (var item in _items) item.PropertyChanged += OnItemChanged;
        UpdateSummary();
    }

    // Characters the user ticked, in list order. The caller maps these onto seats.
    public IReadOnlyList<CharacterPickItem> SelectedCharacters =>
        _items.Where(i => i.IsSelected).ToList();

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CharacterPickItem.IsSelected)) UpdateSummary();
    }

    private void UpdateSummary()
    {
        var count = _items.Count(i => i.IsSelected);
        SummaryText.Text = count == 0
            ? "No characters ticked — this set would have no seats, so Apply is disabled."
            : $"{count} character{(count == 1 ? "" : "s")} ticked — this set will have {count} seat{(count == 1 ? "" : "s")}.";
        OkButton.IsEnabled = count > 0;
    }

    // Filtering hides rows without touching their IsSelected, so a tick made before typing survives
    // the filter being narrowed and then cleared.
    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var text = FilterBox.Text.Trim();
        CharacterList.ItemsSource = text.Length == 0
            ? _items
            : _items.Where(i => i.CharacterName.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // All/None deliberately act on the FILTERED view, so "type a name fragment, hit All" is a usable
    // way to tick a whole squad at once.
    private void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool selected)
    {
        foreach (var item in CharacterList.ItemsSource.OfType<CharacterPickItem>())
            item.IsSelected = selected;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var item in _items) item.PropertyChanged -= OnItemChanged;
        base.OnClosed(e);
    }
}
