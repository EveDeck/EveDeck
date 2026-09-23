using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace EveDeck.Views.Tabs;

// Content of the Hotkeys TabItem, split out of MainWindow.xaml. No code-behind handlers of its own
// (everything is command-bound); HotkeyDataGrid is reached from MainWindow.xaml.cs via this
// UserControl's x:Name in MainWindow.xaml (see ViewModel_PropertyChanged / RegisterHotkeys).
public partial class HotkeysTab : UserControl
{
    public HotkeysTab()
    {
        InitializeComponent();
    }
}
