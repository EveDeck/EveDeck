using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using Color = System.Windows.Media.Color;
using EveDeck.ViewModels;

namespace EveDeck.Views.Tabs;

// Content of the Previews TabItem, split out of MainWindow.xaml. The sidebar section-search
// machinery (ApplySectionSearchFilter / EnsureSectionSearchIndexBuilt / HarvestOptionsSearchText)
// is shared with OptionsTab and stays on MainWindow as internal static helpers; each tab keeps its
// own SectionSearchIndex instance (see MainWindow.xaml.cs's comment on why it's per-tab).
public partial class PreviewsTab : UserControl
{
    public PreviewsTab()
    {
        InitializeComponent();
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    private readonly MainWindow.SectionSearchIndex _previewsSearch = new();

    private void PreviewsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => MainWindow.ApplySectionSearchFilter(_previewsSearch, PreviewsSearchBox, PreviewsMenu, PreviewsSectionsHost, PreviewsNoMatchText);

    // Sections whose content is data-driven change while the app runs, so a once-only index goes
    // stale -- invalidate whenever focus lands in the search box (see MainWindow.xaml.cs's original
    // comment on OptionsSearchBox_GotFocus for the full rationale, which applies here identically).
    private void PreviewsSearchBox_GotFocus(object sender, RoutedEventArgs e)
        => _previewsSearch.Built = false;

    private void FrameColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color })
            ViewModel.ActiveFrameColor = color;
    }

    private void FrameColorPick_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        try
        {
            var current = (Color)System.Windows.Media.ColorConverter.ConvertFromString(ViewModel.ActiveFrameColor);
            dialog.Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B);
        }
        catch { /* keep dialog's default color if the stored hex fails to parse */ }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var c = dialog.Color;
        ViewModel.ActiveFrameColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static string ColorToHex(System.Drawing.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void LabelBackgroundColorPick_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        try
        {
            var current = (Color)System.Windows.Media.ColorConverter.ConvertFromString(ViewModel.LabelBackgroundColor);
            dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        }
        catch { /* keep dialog's default color if the stored hex fails to parse */ }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        ViewModel.LabelBackgroundColor = ColorToHex(dialog.Color);
    }

    private void LabelBackgroundColor2Pick_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        try
        {
            var current = (Color)System.Windows.Media.ColorConverter.ConvertFromString(ViewModel.LabelBackgroundColor2);
            dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        }
        catch { /* keep dialog's default color if the stored hex fails to parse */ }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        ViewModel.LabelBackgroundColor2 = ColorToHex(dialog.Color);
    }

    private void MasterLabelBackgroundColorPick_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        try
        {
            var current = (Color)System.Windows.Media.ColorConverter.ConvertFromString(ViewModel.MasterLabelBackgroundColor);
            dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        }
        catch { /* keep dialog's default color if the stored hex fails to parse */ }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        ViewModel.MasterLabelBackgroundColor = ColorToHex(dialog.Color);
    }

    private void MasterLabelBackgroundColor2Pick_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        try
        {
            var current = (Color)System.Windows.Media.ColorConverter.ConvertFromString(ViewModel.MasterLabelBackgroundColor2);
            dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        }
        catch { /* keep dialog's default color if the stored hex fails to parse */ }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        ViewModel.MasterLabelBackgroundColor2 = ColorToHex(dialog.Color);
    }

    private void LabelFontPick_Click(object sender, RoutedEventArgs e)
    {
        var (family, sizeDip, color) = ViewModel.GlobalLabelFont();
        if (MainWindow.TryPickFont(family, sizeDip, color, out var f, out var s, out var c, ViewModel.Log))
            ViewModel.ApplyGlobalLabelFont(f, s, c);
    }

    // The bundled default is loaded from the exe's resources, not installed, so the Win32 font dialog
    // cannot list it -- this button is the only way back to it once a user picks something else.
    private void LabelFontDefault_Click(object sender, RoutedEventArgs e)
    {
        var (_, sizeDip, color) = ViewModel.GlobalLabelFont();
        ViewModel.ApplyGlobalLabelFont(Models.AppSettings.BundledLabelFontFamily, sizeDip, color);
    }

    private void MasterLabelFontPick_Click(object sender, RoutedEventArgs e)
    {
        var (family, sizeDip, color) = ViewModel.GlobalMasterLabelFont();
        if (MainWindow.TryPickFont(family, sizeDip, color, out var f, out var s, out var c, ViewModel.Log))
            ViewModel.ApplyGlobalMasterLabelFont(f, s, c);
    }

    // Combined "reset to normal" for the Previews-page MASTER label section: clears both the font
    // override and the style/opacity overrides in one click.
    private void MasterLabelStyleAndFontReset_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetGlobalMasterLabelFont();
        ViewModel.ResetGlobalMasterLabelStyle();
    }

    private void InactiveBorderColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color })
            ViewModel.InactivePreviewBorderColor = color;
    }
}
