using System.Globalization;
using System.Windows.Threading;
using EveDeck.Views;

namespace EveDeck.ViewModels;

// Downtime countdown (added 2026-07-24): shows a small always-on-top readout counting down the final
// DowntimeLeadMinutes before EVE's daily downtime, and fires one toast when that window opens. Purely
// a clock -- no ESI, no game interaction; it just compares the wall clock to a configured UTC time.
public sealed partial class MainWindowViewModel
{
    private DowntimeCountdownWindow? _downtimeWindow;
    private readonly DispatcherTimer _downtimeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    // The specific downtime occurrence we've already toasted for, so the "dock up" toast fires once per
    // day (when the lead window opens), not every tick.
    private DateTimeOffset _downtimeToastOccurrence = DateTimeOffset.MinValue;

    private void InitDowntime()
    {
        _downtimeTimer.Tick += (_, _) => DowntimeTick();
        if (_settings.DowntimeCountdownEnabled) _downtimeTimer.Start();
    }

    public bool DowntimeCountdownEnabled
    {
        get => _settings.DowntimeCountdownEnabled;
        set
        {
            if (_settings.DowntimeCountdownEnabled == value) return;
            _settings.DowntimeCountdownEnabled = value;
            OnPropertyChanged();
            Save();
            if (value) { _downtimeTimer.Start(); DowntimeTick(); }
            else { _downtimeTimer.Stop(); HideDowntimeWindow(); }
        }
    }

    public string DowntimeUtcTime
    {
        get => _settings.DowntimeUtcTime;
        set
        {
            var v = (value ?? "").Trim();
            if (_settings.DowntimeUtcTime == v) return;
            _settings.DowntimeUtcTime = v;
            OnPropertyChanged();
            Save();
        }
    }

    public int DowntimeLeadMinutes
    {
        get => _settings.DowntimeLeadMinutes;
        set
        {
            var v = Math.Clamp(value, 1, 1440);
            if (_settings.DowntimeLeadMinutes == v) return;
            _settings.DowntimeLeadMinutes = v;
            OnPropertyChanged();
            Save();
        }
    }

    public string DowntimePosition
    {
        get => _settings.DowntimePosition;
        set
        {
            if (_settings.DowntimePosition == value) return;
            _settings.DowntimePosition = value;
            OnPropertyChanged();
            Save();
            // Drop the current window so the next tick re-creates it at the new anchor.
            HideDowntimeWindow();
            if (_settings.DowntimeCountdownEnabled) DowntimeTick();
        }
    }

    private void DowntimeTick()
    {
        if (!_settings.DowntimeCountdownEnabled) return;

        var nowUtc = DateTimeOffset.UtcNow;
        var next = NextDowntime(nowUtc, _settings.DowntimeUtcTime);
        var remaining = next - nowUtc;
        var lead = TimeSpan.FromMinutes(Math.Clamp(_settings.DowntimeLeadMinutes, 1, 1440));

        if (remaining > TimeSpan.Zero && remaining <= lead)
        {
            ShowDowntimeWindow($"Downtime in {FormatCountdown(remaining)}");
            if (_downtimeToastOccurrence != next)
            {
                _downtimeToastOccurrence = next;
                var mins = (int)Math.Ceiling(remaining.TotalMinutes);
                ShowToast("Downtime soon", $"Downtime in ~{mins} min — dock up / safe up.", "#FDE068");
            }
        }
        else
        {
            HideDowntimeWindow();
        }
    }

    // Next occurrence of the configured UTC downtime time from `nowUtc` (today's if still ahead, else
    // tomorrow's). Falls back to 11:00 UTC if the setting is malformed.
    private static DateTimeOffset NextDowntime(DateTimeOffset nowUtc, string utcTime)
    {
        var t = ParseDowntimeTime(utcTime);
        var today = new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, t.Hours, t.Minutes, 0, TimeSpan.Zero);
        return today > nowUtc ? today : today.AddDays(1);
    }

    private static TimeSpan ParseDowntimeTime(string utcTime)
    {
        if (TimeSpan.TryParse(utcTime?.Trim(), CultureInfo.InvariantCulture, out var ts)
            && ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1))
            return ts;
        return TimeSpan.FromHours(11);
    }

    private static string FormatCountdown(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    private void ShowDowntimeWindow(string text)
    {
        if (_downtimeWindow is null)
        {
            var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId) ?? Monitors.FirstOrDefault();
            if (monitor is null) return;
            var dpiScale = monitor.DpiX / 96.0;
            var anchor = ParseToastAnchor(_settings.DowntimePosition);
            _downtimeWindow = new DowntimeCountdownWindow(
                monitor.WorkArea.X, monitor.WorkArea.Y, monitor.WorkArea.Width, monitor.WorkArea.Height, dpiScale, anchor);
            _downtimeWindow.Show();
        }
        _downtimeWindow.SetText(text);
    }

    private void HideDowntimeWindow()
    {
        if (_downtimeWindow is null) return;
        try { _downtimeWindow.Close(); } catch { /* window may already be gone */ }
        _downtimeWindow = null;
    }
}
