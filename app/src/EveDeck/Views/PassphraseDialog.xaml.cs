using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace EveDeck.Views;

// Passphrase prompt for the ESI character-link export/import. Two modes: export asks twice (a typo
// in a write-once passphrase makes the export permanently useless), import asks once. Mirrors
// RenameProfileDialog's plain code-behind style -- no view-model, no bindings.
//
// The passphrase is deliberately never persisted anywhere: it protects a file full of refresh
// tokens, which are bearer credentials for the character's ESI scopes.
public partial class PassphraseDialog : Window
{
    private readonly bool _confirm;

    public string Passphrase => PassBox.Password;

    public PassphraseDialog(string heading, string blurb, string okLabel, bool confirm)
    {
        InitializeComponent();
        _confirm = confirm;
        Title = heading;
        HeadingText.Text = heading;
        BlurbText.Text = blurb;
        OkButton.Content = okLabel;

        if (!confirm)
        {
            ConfirmLabel.Visibility = Visibility.Collapsed;
            ConfirmBox.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) => PassBox.Focus();
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Commit();
    }

    private void OnOk(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        if (Passphrase.Length < 8)
        {
            Fail("Use at least 8 characters -- this file holds live ESI credentials.");
            return;
        }
        if (_confirm && !string.Equals(Passphrase, ConfirmBox.Password, StringComparison.Ordinal))
        {
            Fail("The two passphrases do not match.");
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
