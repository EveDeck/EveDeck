using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using EveDeck.ViewModels;
using EveDeck.Views;

namespace EveDeck.Views.Tabs;

// Content of the Options TabItem, split out of MainWindow.xaml. UiScaleComboBox and the sidebar
// section-search machinery are reached from MainWindow.xaml.cs via this UserControl's x:Name (see
// SyncUiScaleComboBox / ViewModel_PropertyChanged in MainWindow.xaml.cs).
public partial class OptionsTab : UserControl
{
    public OptionsTab()
    {
        InitializeComponent();
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    private static readonly double[] UiScaleOptions = { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 };

    internal void SyncUiScaleComboBox(double scale)
    {
        var idx = Array.FindIndex(UiScaleOptions, v => Math.Abs(v - scale) < 0.001);
        if (idx >= 0 && UiScaleComboBox.SelectedIndex != idx)
            UiScaleComboBox.SelectedIndex = idx;
    }

    private void UiScaleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UiScaleComboBox.SelectedIndex >= 0 && UiScaleComboBox.SelectedIndex < UiScaleOptions.Length)
            ViewModel.UiScale = UiScaleOptions[UiScaleComboBox.SelectedIndex];
    }

    private readonly MainWindow.SectionSearchIndex _optionsSearch = new();

    private void OptionsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => MainWindow.ApplySectionSearchFilter(_optionsSearch, OptionsSearchBox, OptionsMenu, OptionsSectionsHost, OptionsNoMatchText);

    // Sections whose content is data-driven (Config Profiles, Character Names) change while the app
    // runs, so a once-only index goes stale -- see the original comment on this handler in
    // MainWindow.xaml.cs's history for the full rationale, which applies here identically.
    private void OptionsSearchBox_GotFocus(object sender, RoutedEventArgs e)
        => _optionsSearch.Built = false;

    private void RunSetupWizard_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowSetupWizard();

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var backup = ViewModel.SelectedBackup;
        if (backup is null) return;
        var result = System.Windows.MessageBox.Show(
            $"Restore settings from:\n{backup.DisplayName}\n\nEveDeck will restart. Current settings will be replaced.",
            "Restore Settings Backup",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.OK) return;
        var error = ViewModel.RestoreSelectedBackup();
        if (error is not null)
            System.Windows.MessageBox.Show(
                $"Restore failed:\n{error}",
                "Restore Failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
    }

    private void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CreateBackupNow();
        ViewModel.RefreshBackups();
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Settings",
            Filter = "JSON settings|*.json",
            FileName = $"evedeck_settings_{DateTime.Now:yyyy-MM-dd}.json"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.ExportSettings(dlg.FileName);
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Settings",
            Filter = "JSON settings|*.json"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.ImportSettings(dlg.FileName);
    }

    private void ExportEsiTokens_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Character Links",
            Filter = "EveDeck character links|*.edtok",
            FileName = $"evedeck_characters_{DateTime.Now:yyyy-MM-dd}.edtok"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        var prompt = new PassphraseDialog(
            "Export Character Links",
            "Choose a passphrase for this file. It holds live ESI credentials, and there is no way to recover it if you forget the passphrase.",
            "Export", confirm: true) { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true) return;

        ViewModel.ExportEsiTokens(dlg.FileName, prompt.Passphrase);
    }

    private void ImportEsiTokens_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Character Links",
            Filter = "EveDeck character links|*.edtok"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        var prompt = new PassphraseDialog(
            "Import Character Links",
            "Enter the passphrase this file was exported with.",
            "Import", confirm: false) { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true) return;

        ViewModel.ImportEsiTokens(dlg.FileName, prompt.Passphrase);
    }
}
