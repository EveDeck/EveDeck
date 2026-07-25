using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace EveDeck.Views;

// One-field prompt for renaming a custom layout profile. Built-in presets are not renamable, so the
// view-model gates the command rather than this dialog. Mirrors NewProfileDialog's plain code-behind
// style -- no view-model, no bindings.
public partial class RenameProfileDialog : Window
{
    public string ProfileName => NameBox.Text.Trim();

    public RenameProfileDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        // Preselect so typing replaces the old name outright, which is what a rename usually is.
        Loaded += (_, _) => { NameBox.SelectAll(); NameBox.Focus(); };
    }

    private void OnNameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Commit();
    }

    private void OnOk(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        // An all-whitespace name would leave an unclickable blank row in the profile list.
        if (string.IsNullOrWhiteSpace(ProfileName)) return;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
