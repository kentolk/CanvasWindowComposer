using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class TrayApp : ApplicationContext
{
    private const int ReconcileTimerIntervalMs = 500;
    private const int TrayIconSizePx = 32;
    private const float IconLineWidth = 2f;
    private const int IconArrowLength = 10;
    private const int IconArrowHead = 3;

    private readonly IClock _clock;
    private readonly IScreens _screens;
    private readonly AppConfig _config;
    private readonly NotifyIcon _trayIcon;
    private readonly Timer _bgTimer;
    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private readonly VirtualDesktopService _vds;
    private readonly MinimapOverlay _minimap;
    private readonly SearchOverlay _search;
    private readonly OverviewManager _overview;
    private readonly Win32InputRouter _input;
    private readonly DesktopStateCache _desktops;
    private readonly ForegroundCoordinator _foreground;
    private readonly CanvasNavigator _navigator;
    private bool _enabled = true;

    public TrayApp(IClock? clock = null, IScreens? screens = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _screens = screens ?? WinFormsScreens.Instance;
        _config = new AppConfig();
        _config.Load();
        _config.StartObservingChanges();
        GridRenderer.CompileShaders();
        MinimapRenderer.CompileShaders();

        var winApi = new Win32WindowApi(_screens);
        _vds = new VirtualDesktopService();
        _canvas = new Canvas();
        _input = new Win32InputRouter(_config);
        _wm = new WindowManager(_canvas, winApi, _config, _input, _clock, _vds, useAsyncProjection: true);
        _overview = new OverviewManager(_canvas, _wm, winApi, _input, _config, _screens);
        _overview.Warmup();
        _foreground = new ForegroundCoordinator(_canvas, _overview, _input, _clock, _screens, _config);
        _desktops = new DesktopStateCache(_canvas, _wm, _overview, _vds);
        _navigator = new CanvasNavigator(_canvas, _wm, _input, _screens, _config, _clock);

        // Constructed last so they can self-subscribe to canvas/input/desktops events.
        _minimap = new MinimapOverlay(_canvas, _input, _desktops, winApi, _screens);
        _search = new SearchOverlay(_canvas, _wm, winApi, _input, _screens);

        _bgTimer = new Timer { Interval = ReconcileTimerIntervalMs };
        _bgTimer.Tick += OnBgTick;
        _bgTimer.Start();

        var toggleItem = new ToolStripMenuItem("Enabled", null, OnToggle) { Checked = true };
        var showScreenFixedItem = new ToolStripMenuItem(
            "Show Pinned/Fullscreen While Panning",
            null,
            OnToggleScreenFixedWindowsDuringPan)
        {
            Checked = _config.ShowScreenFixedWindowsDuringPan
        };
        var arrangeGridItem = new ToolStripMenuItem("Arrange in Grid (Ctrl+Alt+G)", null, OnArrangeGrid);
        var autoGridItem = new ToolStripMenuItem("Auto-Grid New Windows", null, OnToggleAutoGrid)
        {
            Checked = _config.AutoGridNewWindows
        };
        var followFocusItem = new ToolStripMenuItem(
            "Follow Focused Windows",
            null,
            OnToggleFollowFocusedWindows)
        {
            Checked = _config.FollowFocusedWindows
        };
        var refreshItem = new ToolStripMenuItem("Refresh", null, OnRefresh);
        var openConfigItem = new ToolStripMenuItem("Open Config Directory", null,
            (_, _) => OpenConfigDirectory());
        var exitItem = new ToolStripMenuItem("Exit", null, OnExit);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripLabel("Canvas Desktop") { Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(toggleItem);
        menu.Items.Add(showScreenFixedItem);
        menu.Items.Add(arrangeGridItem);
        menu.Items.Add(autoGridItem);
        menu.Items.Add(followFocusItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(openConfigItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Canvas Desktop - Middle-click drag to pan",
            ContextMenuStrip = menu,
            Visible = true
        };

        _wm.DiscoverNewWindows();
        _wm.Reproject();
    }

    private void OnBgTick(object? sender, EventArgs e)
    {
        _wm.Tick();
    }

    private void OnToggle(object? sender, EventArgs e)
    {
        _enabled = !_enabled;
        _input.Enabled = _enabled;
        if (!_enabled)
            _overview.CancelInertia();

        if (sender is ToolStripMenuItem item)
            item.Checked = _enabled;

        _trayIcon.Text = _enabled
            ? "Canvas Desktop - Middle-click drag to pan"
            : "Canvas Desktop - Disabled";
    }

    /// <summary>
    /// Lay every canvas window out in a grid of screen-sized cells, then
    /// frame the window that was most recently in front so the user keeps
    /// looking at what they were already looking at.
    /// </summary>
    private void OnArrangeGrid(object? sender, EventArgs e)
    {
        _navigator.ArrangeGrid();
    }

    private void OnRefresh(object? sender, EventArgs e)
    {
        _wm.RefreshAllWindows();
    }

    /// <summary>
    /// Open %APPDATA%\CanvasWindowComposer in Explorer. Uses ArgumentList so the
    /// path is quoted for us — passing it as a raw argument string breaks for
    /// any user whose profile name contains a space.
    /// </summary>
    private static void OpenConfigDirectory()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.ConfigDir);
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(AppConfig.ConfigDir);
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Explorer missing or replaced by a third-party shell — not worth
            // interrupting the user over.
        }
    }

    /// <summary>
    /// Toggle whether newly opened windows join an existing grid. Persisted to
    /// config.ini so the tray and the file agree, matching how the pinned /
    /// fullscreen toggle behaves.
    /// </summary>
    private void OnToggleAutoGrid(object? sender, EventArgs e)
    {
        bool enabled = !_config.AutoGridNewWindows;
        _config.SetAutoGridNewWindows(enabled);

        if (sender is ToolStripMenuItem item)
            item.Checked = enabled;
    }

    /// <summary>
    /// Toggle whether the camera travels to a focused window the user cannot
    /// properly see. Persisted to config.ini so the tray and the file agree.
    /// </summary>
    private void OnToggleFollowFocusedWindows(object? sender, EventArgs e)
    {
        bool enabled = !_config.FollowFocusedWindows;
        _config.SetFollowFocusedWindows(enabled);

        if (sender is ToolStripMenuItem item)
            item.Checked = enabled;
    }

    private void OnToggleScreenFixedWindowsDuringPan(object? sender, EventArgs e)
    {
        bool enabled = !_config.ShowScreenFixedWindowsDuringPan;
        _config.SetShowScreenFixedWindowsDuringPan(enabled);

        if (sender is ToolStripMenuItem item)
            item.Checked = enabled;

        _overview.RefreshConfig();
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _bgTimer.Stop();
        _bgTimer.Dispose();
        _input.Dispose();
        _wm.Reset();
        _wm.Dispose();
        _navigator.Dispose();
        _overview.Dispose();
        _search.Close();
        _minimap.Close();
        _vds.Dispose();
        _config.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(TrayIconSizePx, TrayIconSizePx);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var pen = new Pen(Color.White, IconLineWidth);
        int cx = TrayIconSizePx / 2;
        int cy = TrayIconSizePx / 2;
        int len = IconArrowLength;
        int arrow = IconArrowHead;

        g.DrawLine(pen, cx - len, cy, cx + len, cy);
        g.DrawLine(pen, cx - len, cy, cx - len + arrow, cy - arrow);
        g.DrawLine(pen, cx - len, cy, cx - len + arrow, cy + arrow);
        g.DrawLine(pen, cx + len, cy, cx + len - arrow, cy - arrow);
        g.DrawLine(pen, cx + len, cy, cx + len - arrow, cy + arrow);

        g.DrawLine(pen, cx, cy - len, cx, cy + len);
        g.DrawLine(pen, cx, cy - len, cx - arrow, cy - len + arrow);
        g.DrawLine(pen, cx, cy - len, cx + arrow, cy - len + arrow);
        g.DrawLine(pen, cx, cy + len, cx - arrow, cy + len - arrow);
        g.DrawLine(pen, cx, cy + len, cx + arrow, cy + len - arrow);

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            // Icon.FromHandle doesn't take ownership of the HICON, so returning
            // it directly strands the native handle for the process lifetime.
            // Clone into a self-contained managed Icon and free the original.
            using var native = Icon.FromHandle(hIcon);
            return (Icon)native.Clone();
        }
        finally
        {
            PInvoke.DestroyIcon(new HICON(hIcon));
        }
    }

    private static readonly string LogPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "canvas_debug.log");

    internal static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }
}
