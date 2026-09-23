using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using System.Windows.Input;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using EveDeck.ViewModels;

namespace EveDeck.Views.Tabs;

// Content of the Clients TabItem, split out of MainWindow.xaml (see the note at the top of
// MainWindow.xaml.cs). The window drag-start and mini-map drag-drop handlers stay implemented on
// MainWindow itself -- SeatCardTemplate.xaml.cs already reaches into MainWindow for the same
// mini-map clearing logic (ClearMiniMapDragIndicator), so keeping it there avoids a second copy of
// that shared drag state. These are thin wrappers to satisfy the event wiring, which must resolve
// against this UserControl's own code-behind.
public partial class ClientsTab : UserControl
{
    public ClientsTab()
    {
        InitializeComponent();
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    private void WindowsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.WindowsListBox_PreviewMouseLeftButtonDown(sender, e);

    private void WindowsListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.WindowsListBox_PreviewMouseMove(sender, e);

    private void MiniMapSlot_DragEnter(object sender, DragEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.MiniMapSlot_DragEnter(sender, e);

    private void MiniMapSlot_DragOver(object sender, DragEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.MiniMapSlot_DragOver(sender, e);

    private void MiniMapSlot_DragLeave(object sender, DragEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.MiniMapSlot_DragLeave(sender, e);

    private void MiniMapSlot_Drop(object sender, DragEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.MiniMapSlot_Drop(sender, e);

    // Keyboard equivalent of the seat-card grip drag, so reordering isn't mouse-only. Fully
    // self-contained (only touches this tab's own SlotsListBox and the inherited DataContext), so it
    // moved here verbatim from MainWindow.xaml.cs rather than staying as a wrapper.
    private void SlotsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt) return;
        // Alt-chords route through Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Up && key != Key.Down) return;
        if (ViewModel.SelectedAssignment is not { } selected) return;

        var from = ViewModel.Assignments.IndexOf(selected);
        if (from < 0) return;
        var to = key == Key.Up ? from - 1 : from + 1;
        if (to < 0 || to >= ViewModel.Assignments.Count) return;

        ViewModel.Assignments.Move(from, to);
        ViewModel.Save();
        ViewModel.SelectedAssignment = selected;
        e.Handled = true;

        // Keep keyboard focus on the moved seat's container so repeated Alt+Up/Down keeps walking it.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (SlotsListBox.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem container)
                container.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }
}
