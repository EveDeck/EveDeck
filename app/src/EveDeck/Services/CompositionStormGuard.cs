namespace EveDeck.Services;

// Detects a burst of DWM composition-change broadcasts (WM_DWMCOMPOSITIONCHANGED / WM_DISPLAYCHANGE)
// -- the signature of a GPU driver reset / TDR -- and imposes a cool-off during which the caller must
// NOT re-register its preview sources. Re-registering every DWM thumbnail into a driver that is still
// recovering piles on more work and can provoke a SECOND reset (seen live 2026-09-02: two TDRs 27s
// apart, EveDeck re-registering thumbnails in between). Pulled out of TileSurfaceWindow so the timing
// contract can be unit-tested without a WinForms message pump.
internal sealed class CompositionStormGuard
{
    private readonly int _stormCount;
    private readonly TimeSpan _window;
    private readonly TimeSpan _backoff;
    private readonly Queue<DateTime> _recent = new();
    private DateTime _backoffUntil = DateTime.MinValue;

    public CompositionStormGuard(int stormCount, TimeSpan window, TimeSpan backoff)
    {
        _stormCount = stormCount;
        _window = window;
        _backoff = backoff;
    }

    // True while a cool-off is in effect. The caller should hold any pending re-registration until
    // this goes false rather than dropping it -- one clean re-register once the driver has settled.
    public bool InBackoff(DateTime now) => now < _backoffUntil;

    // Call immediately before a composition-change-driven re-registration would run. Returns true if
    // the caller should SKIP the re-register: either a cool-off is already active, or this refresh is
    // the one that trips the storm threshold (in which case a fresh cool-off starts here).
    public bool RegisterRefreshAndDetectStorm(DateTime now)
    {
        if (now < _backoffUntil) return true;

        _recent.Enqueue(now);
        while (_recent.Count > 0 && now - _recent.Peek() > _window) _recent.Dequeue();

        if (_recent.Count >= _stormCount)
        {
            _recent.Clear();
            _backoffUntil = now + _backoff;
            return true;
        }
        return false;
    }
}
