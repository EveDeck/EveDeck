using System.Windows;
using System.Windows.Controls;
using EveDeck.Models;

namespace EveDeck.Views;

// Prompt for a new custom profile: how many seats, and which shape.
//
// "Master + previews" exists because hand-building it is the single thing people get stuck on. The
// master rect is decided by GEOMETRY (the biggest slot -- see MainWindowViewModel.PickCenterSlot),
// so someone drawing equal rects across two screens gets a master wherever the tie-break lands, with
// no error to explain it. Generating the layout makes the master dominant by construction.
public partial class NewProfileDialog : Window
{
    private readonly IReadOnlyList<MonitorInfo> _monitors;

    public int AccountCount => CountCombo.SelectedItem is int n ? n : 4;

    // True when the user wants the generated two-monitor arrangement rather than an even grid they
    // place themselves. Forced false when there is only one monitor to work with.
    public bool MasterPlusPreviews => MasterPreviewRadio.IsChecked == true && _monitors.Count > 0;

    public MonitorInfo? MasterMonitor => MasterMonitorCombo.SelectedItem as MonitorInfo;
    public MonitorInfo? PreviewMonitor => PreviewMonitorCombo.SelectedItem as MonitorInfo;

    public NewProfileDialog(int defaultCount, IReadOnlyList<MonitorInfo> monitors, string? preferredMasterMonitorId = null)
    {
        InitializeComponent();
        _monitors = monitors;

        foreach (var n in Enumerable.Range(1, 15))
            CountCombo.Items.Add(n);
        CountCombo.SelectedItem = Math.Clamp(defaultCount, 1, 15);

        foreach (var monitor in monitors)
        {
            MasterMonitorCombo.Items.Add(monitor);
            PreviewMonitorCombo.Items.Add(monitor);
        }
        MasterMonitorCombo.DisplayMemberPath = nameof(MonitorInfo.Summary);
        PreviewMonitorCombo.DisplayMemberPath = nameof(MonitorInfo.Summary);

        // Master defaults to the profile's current target monitor (or the primary): the screen the
        // user is most likely playing on. Previews default to any OTHER screen, since a preview grid
        // stacked on top of the master rect is the arrangement nobody is asking for.
        var master = monitors.FirstOrDefault(m => m.Id == preferredMasterMonitorId)
                     ?? monitors.FirstOrDefault(m => m.IsPrimary)
                     ?? monitors.FirstOrDefault();
        MasterMonitorCombo.SelectedItem = master;
        PreviewMonitorCombo.SelectedItem = monitors.FirstOrDefault(m => m.Id != master?.Id) ?? master;

        // One monitor cannot carry a master and a separate preview screen, so the option is not
        // offered at all rather than silently producing an overlapping layout.
        if (monitors.Count < 2)
        {
            MasterPreviewRadio.IsEnabled = false;
            MonitorPanel.IsEnabled = false;
            EvenGridRadio.IsChecked = true;
        }

        MasterPreviewRadio.Checked += OnStyleChanged;
        EvenGridRadio.Checked += OnStyleChanged;
        MasterMonitorCombo.SelectionChanged += OnMonitorChanged;
        PreviewMonitorCombo.SelectionChanged += OnMonitorChanged;
        UpdateStyleState();
    }

    private void OnStyleChanged(object sender, RoutedEventArgs e) => UpdateStyleState();

    private void OnMonitorChanged(object sender, SelectionChangedEventArgs e) => UpdateStyleState();

    private void UpdateStyleState()
    {
        var masterPreviews = MasterPreviewRadio.IsChecked == true;
        MonitorPanel.IsEnabled = masterPreviews && _monitors.Count >= 2;

        // Same monitor on both pickers is allowed (a user may genuinely want to fix it up by hand
        // afterwards) but never silent -- it produces previews sitting on top of the master rect.
        var sameMonitor = masterPreviews
                          && MasterMonitor is not null
                          && MasterMonitor.Id == PreviewMonitor?.Id;
        SameMonitorNote.Visibility = sameMonitor ? Visibility.Visible : Visibility.Collapsed;

        OkButton.Content = masterPreviews ? "Create" : "Create & Place";
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
}
