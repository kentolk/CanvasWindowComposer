using System;
using System.Collections.Generic;
using System.IO;

namespace CanvasDesktop;

/// <summary>
/// User-toggleable feature flags. Implementations may be backed by a config
/// file (<see cref="AppConfig"/>) or a fake (tests).
/// </summary>
internal interface IAppConfig
{
    bool DisableSearch { get; }
    bool DisableAltPan { get; }
    bool DisableGreedyDraw { get; }
    bool ShowScreenFixedWindowsDuringPan { get; }

    /// <summary>
    /// When true, Alt+Q is not registered as a global hotkey, leaving it
    /// available to other apps. The overview can still be opened via the
    /// middle-click drag pan flow.
    /// </summary>
    bool DisableZoomHotkey { get; }

    /// <summary>
    /// When false (default), raw mouse deltas are passed through Windows'
    /// pointer acceleration curve before being applied to the canvas, so pan
    /// speed matches cursor speed and the cursor stays anchored to the same
    /// pixel of any window thumbnail under it. When true, raw HID deltas go
    /// to the canvas directly (1 mouse count = 1 canvas pixel) — useful if
    /// you've turned off "Enhance pointer precision" and want truly linear pan.
    /// </summary>
    bool DisableMouseCurve { get; }

    /// <summary>
    /// Column count for "Arrange in Grid" (Ctrl+Alt+G). Read at each arrange, so
    /// editing config.ini takes effect on the next invocation with no restart.
    /// Clamped to <see cref="GridArranger.MinColumns"/>..<see cref="GridArranger.MaxColumns"/>.
    /// </summary>
    int GridColumns { get; }

    /// <summary>
    /// Opt-in: holding the left button against a screen edge advances the canvas
    /// to the next window, for dragging a file into a window that isn't on
    /// screen. Off by default because it cannot be made precise — an OLE drag is
    /// invisible from outside the source process, so "button held at the edge"
    /// is the only available signal and it also describes selecting text to the
    /// edge of the screen.
    /// </summary>
    bool EnableDragEdgeNavigation { get; }

    /// <summary>
    /// When true, a window opened while the canvas is already a grid drops into
    /// the first free cell. When false the grid is on-demand only — Ctrl+Alt+G
    /// and the tray item still work, but nothing rearranges by itself.
    /// </summary>
    bool AutoGridNewWindows { get; }

    /// <summary>
    /// Whether focusing a window with no pixels on any monitor brings the camera
    /// to it. On, a taskbar-icon click or Alt-Tab travels to a window parked
    /// elsewhere on the canvas, which would otherwise hold focus while the user
    /// had no idea where it went. A window with anything at all on screen is left
    /// alone — they clicked what they could see, so moving the view would be the
    /// surprise. Off, the camera never moves by itself; reach windows with Alt+S,
    /// the minimap, or Ctrl+Alt+arrows instead.
    ///
    /// Does not gate Ctrl+Alt+middle-click, which is the user asking outright.
    /// </summary>
    bool FollowFocusedWindows { get; }

    /// <summary>
    /// Raised after the backing config has been re-read, on the thread that owns
    /// the app (the UI thread for <see cref="AppConfig"/>). Flags that are only
    /// consulted once — hotkey registration, whether the mouse curve exists —
    /// must subscribe to this or they silently ignore edits to config.ini.
    /// </summary>
    event Action? Changed;
}


/// <summary>
/// Reads configuration from %APPDATA%/CanvasWindowComposer/config.ini and
/// reloads on file change. Creates a default config file if it doesn't exist.
/// </summary>
internal sealed class AppConfig : IAppConfig, IDisposable
{
    public static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CanvasWindowComposer");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.ini");
    private const int ReloadDebounceMs = 200;

    private readonly System.Threading.SynchronizationContext? _uiContext;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _reloadTimer;

    public bool DisableSearch { get; private set; }
    public bool DisableAltPan { get; private set; }
    public bool DisableGreedyDraw { get; private set; } = true;
    public bool ShowScreenFixedWindowsDuringPan { get; private set; } = true;
    public bool DisableMouseCurve { get; private set; }
    public bool DisableZoomHotkey { get; private set; }
    public int GridColumns { get; private set; } = GridArranger.DefaultColumns;
    public bool EnableDragEdgeNavigation { get; private set; }
    public bool AutoGridNewWindows { get; private set; } = true;
    public bool FollowFocusedWindows { get; private set; } = true;



    public event Action? Changed;

    /// <remarks>
    /// Construct on the UI thread: the current <see cref="System.Threading.SynchronizationContext"/>
    /// is captured here so file-watcher reloads can apply and notify there
    /// rather than on a thread-pool thread.
    /// </remarks>
    public AppConfig()
    {
        _uiContext = System.Threading.SynchronizationContext.Current;
    }


    public void Load()
    {
        if (!File.Exists(ConfigPath))
        {
            WriteDefault();
            return;
        }

        ApplyValues(ParseIni(ConfigPath));
    }

    internal void ApplyValues(Dictionary<string, string> values)
    {
        DisableSearch = GetBool(values, "DisableSearch", defaultValue: false);
        DisableAltPan = GetBool(values, "DisableAltPan", defaultValue: false);
        DisableGreedyDraw = GetBool(values, "DisableGreedyDraw", defaultValue: true);
        ShowScreenFixedWindowsDuringPan = GetBool(values, "ShowScreenFixedWindowsDuringPan", defaultValue: true);
        DisableMouseCurve = GetBool(values, "DisableMouseCurve", defaultValue: false);
        DisableZoomHotkey = GetBool(values, "DisableZoomHotkey", defaultValue: false);
        GridColumns = GetInt(values, "GridColumns", GridArranger.DefaultColumns,
            GridArranger.MinColumns, GridArranger.MaxColumns);
        EnableDragEdgeNavigation = GetBool(values, "EnableDragEdgeNavigation", defaultValue: false);
        AutoGridNewWindows = GetBool(values, "AutoGridNewWindows", defaultValue: true);
        FollowFocusedWindows = GetBool(values, "FollowFocusedWindows", defaultValue: true);


    }



    public void SetShowScreenFixedWindowsDuringPan(bool value)
    {
        ShowScreenFixedWindowsDuringPan = value;
        WriteBool("ShowScreenFixedWindowsDuringPan", value);
    }

    public void SetAutoGridNewWindows(bool value)
    {
        AutoGridNewWindows = value;
        WriteBool("AutoGridNewWindows", value);
    }

    public void SetFollowFocusedWindows(bool value)
    {
        FollowFocusedWindows = value;
        WriteBool("FollowFocusedWindows", value);
    }


    /// <summary>Watch config.ini for changes and reload automatically.</summary>
    public void StartObservingChanges()
    {
        Directory.CreateDirectory(ConfigDir);

        _reloadTimer = new System.Threading.Timer(
            _ => ReloadFromDisk(), null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

        _watcher = new FileSystemWatcher(ConfigDir, "config.ini")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnConfigFileChanged;
        _watcher.Created += OnConfigFileChanged;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // Trailing edge: every event pushes the reload further out, so we read
        // once after the writer has finished. A leading-edge debounce reads the
        // first event — often a truncated or still-locked file — and then drops
        // the follow-up event that actually carries the new contents, which is
        // why edits sometimes appeared to be ignored entirely.
        _reloadTimer?.Change(ReloadDebounceMs, System.Threading.Timeout.Infinite);
    }

    /// <summary>
    /// Timer-thread callback: parse off-thread, then apply and notify on the UI
    /// thread so subscribers (hotkey registration, mouse-curve swap) run where
    /// they can touch window state.
    /// </summary>
    private void ReloadFromDisk()
    {
        Dictionary<string, string> values;
        try
        {
            values = ParseIni(ConfigPath);
        }
        catch (IOException)
        {
            // Still locked by the writer — come back rather than dropping it.
            _reloadTimer?.Change(ReloadDebounceMs, System.Threading.Timeout.Infinite);
            return;
        }
        catch
        {
            return;
        }

        if (_uiContext != null)
            _uiContext.Post(_ => ApplyAndNotify(values), null);
        else
            ApplyAndNotify(values);
    }

    private void ApplyAndNotify(Dictionary<string, string> values)
    {
        ApplyValues(values);
        Changed?.Invoke();
    }


    public void Dispose()
    {
        _reloadTimer?.Dispose();
        _reloadTimer = null;
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnConfigFileChanged;
        _watcher.Created -= OnConfigFileChanged;
        _watcher.Dispose();
        _watcher = null;
    }

    private static void WriteDefault()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath,
@"[CanvasWindowComposer]
; All flags below are commented out and set to their default values.
; Uncomment a line and flip to true to opt out of the feature.

; Disable Alt+S window search
;DisableSearch=false

; Disable Alt+middle-click pan over windows
;DisableAltPan=false

; Disable SetWindowRgn clipping for off-screen windows
; Retains Alt-Tab and taskbar thumbnails at expense of performance
; Default on - with clipping enabled, you might see gray windows,
; if this app terminates unexpectedly
;DisableGreedyDraw=true

; Show pinned, maximized/fullscreen, and supported screen-fixed panels while panning
;ShowScreenFixedWindowsDuringPan=true

; Disable Windows pointer acceleration curve on pan deltas
; (default off = curve on, pan tracks cursor; on = raw HID deltas)
;DisableMouseCurve=false

; Disable the Alt+Q overview/zoom global hotkey
; (frees Alt+Q for other apps; the overview is still reachable via pan)
;DisableZoomHotkey=false

; Columns used by Arrange in Grid (Ctrl+Alt+G). Each cell is one monitor
; work area, so windows fill the screen without being maximized.
; Clamped to 1-32.
;GridColumns=3

; Hold the left mouse button against a screen edge to advance to the next
; window - for dragging a file into a window that is off screen. Opt-in:
; a drag in another app is not observable from here, so this also triggers
; if you hold the button at the edge for other reasons.
;EnableDragEdgeNavigation=false

; Drop newly opened windows into the next free grid cell, but only while the
; canvas is already a grid. Set false for on-demand only (Ctrl+Alt+G).
; Dialogs and fixed-size windows are never auto-placed either way.
;AutoGridNewWindows=true

; Bring the camera to a focused window that is entirely off screen - a taskbar
; icon click or Alt-Tab for a window parked elsewhere on the canvas. A window
; with anything at all on screen is never chased. Set false to stop the camera
; moving on its own; Ctrl+Alt+middle-click still works either way.
;FollowFocusedWindows=true
");
    }

    private static Dictionary<string, string> ParseIni(string path)
    {
        return ParseIniLines(File.ReadAllLines(path));
    }

    /// <summary>
    /// Parse key=value lines, skipping blanks, ';'/'#' comments and '[section]'
    /// headers. Later duplicates win. Keys are case-insensitive.
    /// </summary>
    internal static Dictionary<string, string> ParseIniLines(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[')
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            result[key] = val;
        }

        return result;
    }

    internal static bool GetBool(Dictionary<string, string> values, string key, bool defaultValue)
    {
        if (!values.TryGetValue(key, out string? val))
            return defaultValue;
        return val.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Read an integer setting, clamped to [<paramref name="min"/>, <paramref name="max"/>].
    /// Anything unparseable falls back to the default rather than throwing — a
    /// typo in config.ini should not take the app down on a reload.
    /// </summary>
    internal static int GetInt(Dictionary<string, string> values, string key, int defaultValue, int min, int max)
    {
        if (!values.TryGetValue(key, out string? raw))
            return defaultValue;
        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int parsed))
            return defaultValue;
        return Math.Clamp(parsed, min, max);
    }

    private static void WriteBool(string key, bool value)

    {
        Directory.CreateDirectory(ConfigDir);
        if (!File.Exists(ConfigPath))
            WriteDefault();

        string[] lines = File.ReadAllLines(ConfigPath);
        string replacement = $"{key}={value.ToString().ToLowerInvariant()}";
        bool replaced = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimStart();
            if (line.StartsWith(';') || line.StartsWith('#'))
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            string existingKey = line[..eq].Trim();
            if (!existingKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            lines[i] = replacement;
            replaced = true;
            break;
        }

        if (!replaced)
        {
            var updated = new List<string>(lines.Length + 2);
            updated.AddRange(lines);
            updated.Add("");
            updated.Add(replacement);
            lines = updated.ToArray();
        }

        File.WriteAllLines(ConfigPath, lines);
    }
}
