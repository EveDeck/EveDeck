using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using EveDeck.ViewModels;

namespace EveDeck.Views.Tabs;

// Content of the Layouts TabItem, split out of MainWindow.xaml.
public partial class LayoutsTab : UserControl
{
    public LayoutsTab()
    {
        InitializeComponent();
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    // LayoutSlot is a plain model with no change notification, so edits in the slot table (size,
    // "Renders as") would leave the minimum-size warning stale until the layout was reselected.
    // CellEditEnding fires before the value commits, hence the deferred refresh.
    private void SlotsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        => Dispatcher.BeginInvoke(ViewModel.RaiseLayoutModeDependents, System.Windows.Threading.DispatcherPriority.Background);

    // F2 renames the selected custom profile, matching the shell convention. The command's own
    // CanExecute keeps built-in presets out of it, so no extra check here.
    private void ProfilesListBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.F2) return;
        if (!ViewModel.RenameProfileCommand.CanExecute(null)) return;
        ViewModel.RenameProfileCommand.Execute(null);
        e.Handled = true;
    }
}
