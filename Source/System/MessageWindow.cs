using System;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Single hidden NativeWindow that handles WM_HOTKEY (Alt+S search, Alt+Q overview).
/// </summary>
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    private const int HOTKEY_SEARCH = 1;
    private const int HOTKEY_OVERVIEW = 2;
    private const int HOTKEY_ESCAPE = 3;
    private const int HOTKEY_NAV_LEFT = 4;
    private const int HOTKEY_NAV_RIGHT = 5;
    private const int HOTKEY_NAV_UP = 6;
    private const int HOTKEY_NAV_DOWN = 7;
    private const int HOTKEY_ARRANGE_GRID = 8;

    private const uint VK_S = 0x53;
    private const uint VK_Q = 0x51;
    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_LEFT = 0x25;
    private const uint VK_UP = 0x26;
    private const uint VK_RIGHT = 0x27;
    private const uint VK_DOWN = 0x28;
    private const uint VK_G = 0x47;


    private Action? _onSearchHotkey;
    private Action? _onOverviewHotkey;
    private Action? _onEscHotkey;
    private bool _escRegistered;
    private Action<NavDirection>? _onNavigate;
    private Action? _onArrangeGrid;
    private bool _navRegistered;

    private bool _searchRegistered;
    private bool _overviewRegistered;

    public MessageWindow()
    {
        CreateHandle(new CreateParams());
    }

    /// <summary>
    /// Point the Alt+S / Alt+Q hotkeys at the given callbacks. A null callback
    /// means "don't register this hotkey" — it stays free for other apps.
    ///
    /// Safe to call repeatedly: DisableSearch / DisableZoomHotkey can be edited
    /// in config.ini while the app is running, so each hotkey is registered or
    /// released to match whatever it was handed this time.
    /// </summary>
    public void RegisterHandlers(Action? onSearchHotkey, Action? onOverviewHotkey)
    {
        _onSearchHotkey = onSearchHotkey;
        _onOverviewHotkey = onOverviewHotkey;

        const HOT_KEY_MODIFIERS modifiers = HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        _searchRegistered = ApplyHotkey(_searchRegistered, onSearchHotkey != null, HOTKEY_SEARCH, modifiers, VK_S);
        _overviewRegistered = ApplyHotkey(_overviewRegistered, onOverviewHotkey != null, HOTKEY_OVERVIEW, modifiers, VK_Q);
    }

    /// <summary>Register or unregister one hotkey to match <paramref name="wanted"/>. Returns the new state.</summary>
    private bool ApplyHotkey(bool registered, bool wanted, int id, HOT_KEY_MODIFIERS modifiers, uint vk)
    {
        if (wanted == registered) return registered;
        if (!wanted)
        {
            PInvoke.UnregisterHotKey((HWND)Handle, id);
            return false;
        }
        // Registration can legitimately fail if another app already owns the
        // combination; report what actually happened so a later toggle retries.
        return PInvoke.RegisterHotKey((HWND)Handle, id, modifiers, vk);
    }


    /// <summary>
    /// Register the canvas navigation hotkeys: Ctrl+Alt+Arrow to step between
    /// windows, Ctrl+Alt+G to arrange the grid.
    ///
    /// Ctrl+Alt rather than the more obvious Win+Arrow because the shell owns
    /// Win+Arrow for Snap and RegisterHotKey fails with
    /// ERROR_HOTKEY_ALREADY_REGISTERED for it. The only way to take those keys
    /// is a low-level keyboard hook that swallows them before the shell, which
    /// would break Snap system-wide and put every keystroke through our UI
    /// thread - not worth it for a binding.
    /// </summary>
    public void RegisterNavigationHandlers(Action<NavDirection>? onNavigate, Action? onArrangeGrid)
    {
        _onNavigate = onNavigate;
        _onArrangeGrid = onArrangeGrid;

        bool wanted = onNavigate != null || onArrangeGrid != null;
        if (wanted == _navRegistered) return;

        const HOT_KEY_MODIFIERS modifiers =
            HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT;

        if (!wanted)
        {
            PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_NAV_LEFT);
            PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_NAV_RIGHT);
            PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_NAV_UP);
            PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_NAV_DOWN);
            PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_ARRANGE_GRID);
            _navRegistered = false;
            return;
        }

        PInvoke.RegisterHotKey((HWND)Handle, HOTKEY_NAV_LEFT, modifiers, VK_LEFT);
        PInvoke.RegisterHotKey((HWND)Handle, HOTKEY_NAV_RIGHT, modifiers, VK_RIGHT);
        PInvoke.RegisterHotKey((HWND)Handle, HOTKEY_NAV_UP, modifiers, VK_UP);
        PInvoke.RegisterHotKey((HWND)Handle, HOTKEY_NAV_DOWN, modifiers, VK_DOWN);
        PInvoke.RegisterHotKey((HWND)Handle, HOTKEY_ARRANGE_GRID, modifiers, VK_G);
        _navRegistered = true;
    }

    /// <summary>
    /// Register Esc as a global hotkey. Caller is responsible for pairing this
    /// with <see cref="DisableEscHotkey"/> when the gate (e.g. overview open)
    /// closes — Esc is heavily used by other apps and we shouldn't hold it
    /// outside the moments we actually consume it.
    /// </summary>
    public void EnableEscHotkey(Action onEsc)
    {
        _onEscHotkey = onEsc;
        if (_escRegistered) return;
        const HOT_KEY_MODIFIERS modifiers = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        _escRegistered = PInvoke.RegisterHotKey((HWND)Handle, HOTKEY_ESCAPE, modifiers, VK_ESCAPE);
    }

    public void DisableEscHotkey()
    {
        if (_escRegistered)
        {
            PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_ESCAPE);
            _escRegistered = false;
        }
        _onEscHotkey = null;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)PInvoke.WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case HOTKEY_SEARCH:
                    _onSearchHotkey?.Invoke();
                    return;
                case HOTKEY_OVERVIEW:
                    _onOverviewHotkey?.Invoke();
                    return;
                case HOTKEY_ESCAPE:
                    _onEscHotkey?.Invoke();
                    return;
                case HOTKEY_NAV_LEFT:
                    _onNavigate?.Invoke(NavDirection.Left);
                    return;
                case HOTKEY_NAV_RIGHT:
                    _onNavigate?.Invoke(NavDirection.Right);
                    return;
                case HOTKEY_NAV_UP:
                    _onNavigate?.Invoke(NavDirection.Up);
                    return;
                case HOTKEY_NAV_DOWN:
                    _onNavigate?.Invoke(NavDirection.Down);
                    return;
                case HOTKEY_ARRANGE_GRID:
                    _onArrangeGrid?.Invoke();
                    return;

            }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_searchRegistered)
            PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_SEARCH);
        if (_overviewRegistered)
            PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_OVERVIEW);
        if (_escRegistered)
            PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_ESCAPE);
        if (_navRegistered)
            RegisterNavigationHandlers(null, null);
        DestroyHandle();

    }
}
