using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using EveDeck.Models;

namespace EveDeck.Services;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;

    // All valid bindings get a stable id up-front; OS registration is tracked separately so the
    // gated subset can be registered/unregistered as the foreground window changes.
    private readonly Dictionary<int, HotkeyBinding> _allById = new();
    private readonly HashSet<int> _registeredIds = new();
    private readonly List<int> _alwaysIds = new();
    private readonly List<int> _gatedIds = new();

    private HwndSource? _source;
    private nint _windowHandle;
    private int _nextId = 100;

    // Foreground-gating state.
    private bool _requireEveFocus;
    private Func<bool>? _isEveForeground;
    private LogService? _log;
    private bool _gatedActive;

    // How long the foreground must stay away from EVE before the gated hotkeys are actually dropped.
    // Long enough to swallow a seat swap's transient, short enough that a real alt-tab hands the keys
    // back to the other app promptly. During this window a gated hotkey can still fire while another
    // app has focus -- a deliberate trade, and the same one the preview hide-delay already makes.
    private static readonly TimeSpan GateDropDelay = TimeSpan.FromMilliseconds(400);
    private DispatcherTimer? _gateDropTimer;

    private nint _winEventHook;
    private WinEventDelegate? _winEventProc;   // kept alive to prevent GC of the callback

    public event EventHandler<HotkeyBinding>? HotkeyPressed;
    // Fired whenever the OS foreground window changes. Carries the new foreground HWND.
    public event EventHandler<nint>? ForegroundChanged;

    // FocusSlot and SwitchToCharacter are the primary ways to bring an EVE client to focus from
    // another app, so they're always registered even when EVE-focus gating is on.
    private static bool IsAlwaysOn(HotkeyBinding b) =>
        b.ActionId.StartsWith("FocusSlot", StringComparison.OrdinalIgnoreCase)
        || b.ActionId.StartsWith("SwitchToCharacter", StringComparison.OrdinalIgnoreCase)
        // The suspend/resume toggle must fire from anywhere so it can un-suspend the rest.
        || b.ActionId.Equals("ToggleHotkeysSuspended", StringComparison.OrdinalIgnoreCase);

    // Returns the display text for every binding that could not be registered (empty if all succeeded)
    // so the caller can surface the full set to the user, not just the first conflict.
    public IReadOnlyList<string> RegisterAll(nint windowHandle, IEnumerable<HotkeyBinding> bindings, LogService log,
        bool requireEveFocus, Func<bool> isEveForeground, bool suspended = false)
    {
        UnregisterAll(log);
        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WndProc);
        _log = log;
        _requireEveFocus = requireEveFocus;
        _isEveForeground = isEveForeground;

        var failures = new List<string>();
        var seenGestures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings.Where(b => b.Enabled && b.VirtualKey != 0))
        {
            // While suspended, only the suspend/resume toggle stays registered so the user can
            // turn hotkeys back on; everything else is skipped entirely.
            if (suspended && !binding.ActionId.Equals("ToggleHotkeysSuspended", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                SafetyGuard.ThrowIfInputBroadcastAction(binding.ActionId);
                var gestureKey = $"{binding.Modifiers}:{binding.VirtualKey}";
                if (!seenGestures.Add(gestureKey))
                {
                    failures.Add($"{binding.DisplayName} ({binding.GestureText}) duplicates another EveDeck hotkey.");
                    continue;
                }

                var id = _nextId++;
                _allById[id] = binding;
                (IsAlwaysOn(binding) ? _alwaysIds : _gatedIds).Add(id);
            }
            catch (Exception ex)
            {
                failures.Add($"{binding.DisplayName} ({binding.GestureText}): {ex.Message}");
            }
        }

        if (_requireEveFocus)
        {
            // Always-on hotkeys register immediately; gated ones follow the foreground window.
            foreach (var id in _alwaysIds) TryRegister(id, failures);
            InstallForegroundHook();
            UpdateGatedState();
        }
        else
        {
            foreach (var id in _allById.Keys) TryRegister(id, failures);
        }

        if (failures.Count > 0)
        {
            log.Error($"{failures.Count} hotkey(s) were not registered. Another app or another EveDeck instance may already own them: {string.Join("; ", failures)}");
        }

        return failures;
    }

    public void UnregisterAll(LogService? log = null)
    {
        RemoveForegroundHook();
        // A pending gate drop must not survive this: hotkey capture unregisters everything and then
        // re-registers, and a timer left armed from before would fire afterwards and silently strip
        // the gated set back off again.
        _gateDropTimer?.Stop();

        foreach (var id in _registeredIds.ToList())
        {
            UnregisterHotKey(_windowHandle, id);
        }

        if (_registeredIds.Count > 0)
        {
            log?.Info($"Unregistered {_registeredIds.Count} hotkeys.");
        }

        _registeredIds.Clear();
        _allById.Clear();
        _alwaysIds.Clear();
        _gatedIds.Clear();
        _gatedActive = false;

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    public void Dispose() => UnregisterAll();

    private void TryRegister(int id, List<string> failures)
    {
        if (_registeredIds.Contains(id)) return;
        var binding = _allById[id];
        if (RegisterHotKey(_windowHandle, id, binding.Modifiers, binding.VirtualKey))
        {
            _registeredIds.Add(id);
            _log?.Info($"Registered hotkey {binding.GestureText} for {binding.DisplayName}.");
        }
        else
        {
            failures.Add($"{binding.DisplayName} ({binding.GestureText}): {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
    }

    private void Unregister(int id)
    {
        if (_registeredIds.Remove(id)) UnregisterHotKey(_windowHandle, id);
    }

    // Registers/unregisters the gated subset to match whether an EVE client is foreground.
    //
    // Raising the gate is immediate; DROPPING it is delayed. The foreground leaves EVE constantly in
    // normal play -- every seat swap passes through a moment where no EVE client is foreground -- and
    // acting on each of those tore down and rebuilt every gated registration, then did it again
    // milliseconds later. One short session logged 223 registrations, 12% of the whole log. Because
    // the re-raise is immediate, gated hotkeys are never missing while you are actually in game; a
    // flap that resolves inside the delay window now costs nothing at all. Same reasoning as
    // HidePreviewsOnFocusLossDelaySeconds, which exists for this identical transient.
    private void UpdateGatedState()
    {
        if (!_requireEveFocus) return;
        var eveForeground = _isEveForeground?.Invoke() ?? false;

        if (eveForeground)
        {
            _gateDropTimer?.Stop();   // back in EVE -- cancel any pending drop
            if (_gatedActive) return;
            _gatedActive = true;

            var failures = new List<string>();
            foreach (var id in _gatedIds) TryRegister(id, failures);
            if (failures.Count > 0) _log?.Error($"Could not register {failures.Count} EVE-focus hotkey(s): {failures[0]}");
            return;
        }

        if (!_gatedActive) return;
        _gateDropTimer ??= CreateGateDropTimer();
        _gateDropTimer.Stop();
        _gateDropTimer.Start();
    }

    private DispatcherTimer CreateGateDropTimer()
    {
        var timer = new DispatcherTimer { Interval = GateDropDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Re-check rather than trusting the state that armed the timer: the whole point is that
            // the foreground may have returned to EVE in the meantime.
            if (_isEveForeground?.Invoke() ?? false) return;
            if (!_gatedActive) return;
            _gatedActive = false;
            foreach (var id in _gatedIds) Unregister(id);
        };
        return timer;
    }

    private void InstallForegroundHook()
    {
        if (_winEventHook != 0) return;
        _winEventProc = OnForegroundChanged;
        _winEventHook = SetWinEventHook(EventSystemForeground, EventSystemForeground,
            0, _winEventProc, 0, 0, WineventOutOfContext);
    }

    private void RemoveForegroundHook()
    {
        if (_winEventHook != 0)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = 0;
        }
        _winEventProc = null;
    }

    private void OnForegroundChanged(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        UpdateGatedState();
        ForegroundChanged?.Invoke(this, hwnd);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && _allById.TryGetValue(wParam.ToInt32(), out var binding))
        {
            handled = true;
            HotkeyPressed?.Invoke(this, binding);
        }

        return 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    private delegate void WinEventDelegate(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hWinEventHook);
}
