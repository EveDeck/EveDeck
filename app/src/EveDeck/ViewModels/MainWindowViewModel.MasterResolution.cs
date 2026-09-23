using System.Collections.ObjectModel;
using EveDeck.Models;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ── Master resolution picker (VSR/DSR supersampling) ───────────────────────

    private ObservableCollection<DisplayModeOption> _availableMasterResolutions = new();
    public ObservableCollection<DisplayModeOption> AvailableMasterResolutions
    {
        get => _availableMasterResolutions;
        private set { _availableMasterResolutions = value; OnPropertyChanged(); }
    }

    private DisplayModeOption? _selectedMasterResolution;
    public DisplayModeOption? SelectedMasterResolution
    {
        get => _selectedMasterResolution;
        set
        {
            if (SetProperty(ref _selectedMasterResolution, value))
                SetMasterResolutionCommand.RaiseCanExecuteChanged();
        }
    }

    public string MasterResolutionStatus
    {
        get
        {
            if (SelectedProfile is null) return "No profile selected.";
            if (SelectedProfile.MasterResolutionWidth > 0)
            {
                var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
                    ?? Monitors.FirstOrDefault(m => m.IsPrimary);
                var w = SelectedProfile.MasterResolutionWidth;
                var h = SelectedProfile.MasterResolutionHeight;
                if (monitor is not null && (w > monitor.Bounds.Width || h > monitor.Bounds.Height))
                    return $"Warning: stored override {w}x{h} is larger than your current desktop ({monitor.Bounds.Width}x{monitor.Bounds.Height}) and will be clamped. To use {w}x{h}: enable AMD VSR or Nvidia DSR, switch Windows display resolution to {w}x{h}, then apply layout.";
                return $"Override active: master slot will be placed at {w}x{h}. Set EVE Fixed Window to match, restart clients, then click Apply Layout.";
            }
            return "Auto — master slot scales to fill the target monitor. The picker above shows all modes that fit your current desktop. To unlock higher VSR/DSR resolutions, switch your Windows display resolution to the virtual size first.";
        }
    }

    // "warn" | "active" | "info" — drives color triggers in Display Tips XAML.
    public string MasterResolutionStatusSeverity
    {
        get
        {
            if (SelectedProfile is null) return "info";
            if (SelectedProfile.MasterResolutionWidth > 0)
            {
                var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
                    ?? Monitors.FirstOrDefault(m => m.IsPrimary);
                var w = SelectedProfile.MasterResolutionWidth;
                var h = SelectedProfile.MasterResolutionHeight;
                if (monitor is not null && (w > monitor.Bounds.Width || h > monitor.Bounds.Height))
                    return "warn";
                return "active";
            }
            return "info";
        }
    }

    private void LoadMasterResolutions()
    {
        var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();

        if (monitor is null) return;

        var desktopW = monitor.Bounds.Width;
        var desktopH = monitor.Bounds.Height;

        var seenNative = new HashSet<(int, int)>();
        var seenVsr = new HashSet<(int, int)>();
        var modesNative = new List<DisplayModeOption>();
        var modesVsr = new List<DisplayModeOption>();
        var dm = new Utilities.Win32Native.DEVMODE();
        dm.dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<Utilities.Win32Native.DEVMODE>();
        dm.dmDeviceName = "";
        dm.dmFormName = "";
        uint modeNum = 0;
        while (Utilities.Win32Native.EnumDisplaySettingsEx(monitor.DeviceName, modeNum, ref dm, 0))
        {
            if (dm.dmBitsPerPel == 32 && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
            {
                int w = (int)dm.dmPelsWidth, h = (int)dm.dmPelsHeight;
                if (w <= desktopW && h <= desktopH)
                {
                    if (seenNative.Add((w, h)))
                        modesNative.Add(new DisplayModeOption($"{w}×{h}", w, h));
                }
                else
                {
                    // Above desktop bounds: VSR/DSR virtual resolution — desktop must be switched to this
                    // resolution in Windows Display Settings before EveDeck can place windows at that size.
                    if (seenVsr.Add((w, h)))
                        modesVsr.Add(new DisplayModeOption($"{w}×{h} (switch desktop to use)", w, h));
                }
            }
            modeNum++;
        }

        // Build final list: current desktop first, then larger-area VSR/DSR options, then remaining native.
        var desktopLabel = $"{desktopW}×{desktopH} (current desktop)";
        var desktopMode = new DisplayModeOption(desktopLabel, desktopW, desktopH);

        var all = new List<DisplayModeOption> { desktopMode };
        all.AddRange(modesVsr.OrderByDescending(m => (long)m.Width * m.Height));
        all.AddRange(modesNative
            .Where(m => !(m.Width == desktopW && m.Height == desktopH))
            .OrderByDescending(m => (long)m.Width * m.Height));

        AvailableMasterResolutions = new ObservableCollection<DisplayModeOption>(all);

        // Pre-select the item that matches the profile's stored override (if any).
        if (SelectedProfile?.MasterResolutionWidth > 0)
            _selectedMasterResolution = all.FirstOrDefault(m => m.Width == SelectedProfile.MasterResolutionWidth && m.Height == SelectedProfile.MasterResolutionHeight);
        else
            _selectedMasterResolution = desktopMode;
        OnPropertyChanged(nameof(SelectedMasterResolution));
    }

    private void ExecuteSetMasterResolution()
    {
        if (SelectedMasterResolution is null || SelectedProfile is null) return;
        SelectedProfile.MasterResolutionWidth = SelectedMasterResolution.Width;
        SelectedProfile.MasterResolutionHeight = SelectedMasterResolution.Height;
        OnPropertyChanged(nameof(MasterResolutionStatus));
        OnPropertyChanged(nameof(MasterResolutionStatusSeverity));
        ClearMasterResolutionCommand.RaiseCanExecuteChanged();
        Save();
        Log.Info($"Master resolution override set to {SelectedMasterResolution.Width}×{SelectedMasterResolution.Height} for profile '{SelectedProfile.Name}'.");
    }

    private void ExecuteClearMasterResolution()
    {
        if (SelectedProfile is null) return;
        SelectedProfile.MasterResolutionWidth = 0;
        SelectedProfile.MasterResolutionHeight = 0;
        LoadMasterResolutions();
        OnPropertyChanged(nameof(MasterResolutionStatus));
        OnPropertyChanged(nameof(MasterResolutionStatusSeverity));
        ClearMasterResolutionCommand.RaiseCanExecuteChanged();
        Save();
        Log.Info($"Master resolution override cleared for profile '{SelectedProfile.Name}' (auto-fill restored).");
    }
}
