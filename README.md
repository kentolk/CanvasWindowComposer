# Canvas Window Composer

> **Note:** This project is in very early development. Expect rough edges, bugs, and breaking changes.

Turns your Windows desktop into an infinite, pannable, zoomable canvas. Middle-click drag to pan all windows, Alt+scroll to zoom.

![Panning](Docs/canvasdesktop-pan.gif)

![Alt+S Search](Docs/canvasdesktop-alts.gif)

## Features

- **Pan** — Middle-click drag on desktop, or Alt+middle-click anywhere
- **Inertia** — Fling to keep sliding, smooth deceleration
- **Zoom** — Alt+scroll to zoom in/out around the cursor
- **Fuzzy search** — Alt+S to find and jump to any window
- **Minimap** — Canvas overview with app icons, fades after inactivity
- **Virtual desktops** — Independent canvas per desktop
- **Auto-focus** — Camera follows focused windows
- **Off-screen hiding** — Windows hidden when panned out of view
- **Grid layout** — Arrange windows into screen-sized cells; jump between them with Ctrl+Alt+arrows
- **Edge navigation** — Optional: hold a drag against a screen edge to advance to the next window
- **System tray** — Toggle, reset, exit

## Controls

| Input | Action |
|---|---|
| Middle-click drag on desktop | Pan all windows |
| Alt + middle-click drag anywhere | Pan (works over windows) |
| Alt + Q | Toggle overview (map-view) |
| Esc | Close the overview |
| Alt + scroll | Zoom in/out around cursor (opens overview if closed) |
| Alt + S | Fuzzy window search |
| Alt + S, then tap S | Cycle the highlighted result; release Alt to jump to it |
| Alt + S, then Ctrl + Space | Pin/unpin the highlighted window to the screen |
| Alt + S, then up/down, Enter | Move through results and jump to one |
| Ctrl + Alt + G | Arrange all windows into a grid of screen-sized cells |
| Ctrl + Alt + arrows | Jump to the neighbouring window |
| Tray menu > Enabled | Toggle the canvas on/off |
| Tray menu > Refresh | Unclip and redraw all windows |
| Tray menu > Arrange in Grid | Same as Ctrl+Alt+G |
| Tray menu > Auto-Grid New Windows | Whether new windows join an existing grid |
| Tray menu > Show Pinned/Fullscreen While Panning | Whether screen-fixed windows stay visible during a pan |
| Tray menu > Open Config Directory | Open the config folder |

## How it works

**The overview is a fake desktop made of live DWM thumbnails.** When you pan or press Alt+Q, a borderless form per monitor comes up with a D3D11 swap chain. Instead of rendering window contents ourselves, we call `DwmRegisterThumbnail` for the desktop wallpaper (Progman/WorkerW), every canvas-managed window, and the taskbar(s) — DWM then composites live thumbnails onto the form. We only push destination rects when the camera moves; the thumbnails stay in sync at the source window's own frame rate, with no pixel copy. The real windows stay parked wherever `SetWindowRgn` clipped them — the overview is a view on top of that state, not a replacement for it.

**Pan and zoom share one camera, the overview adds a second on top.** In *panning* mode the overlay is click-through (`WS_EX_TRANSPARENT`), so middle-click drag keeps driving the real canvas camera and all the thumbnails reflow in real time — including windows that would otherwise be clipped off-screen. In *zooming* mode (Alt+Q / Alt+scroll) click-through goes off and an HLSL shader draws an adaptive grid, scale marks, and a nebula parallax; a second *overview camera* decouples from the canvas camera so you can zoom out further than the real screen would allow for a map-level view. Clicking a thumbnail (or arrow-keys + Enter) recenters the canvas on that window and closes the overlay.

## Config

A default `config.ini` is written to `%APPDATA%\CanvasWindowComposer\` on first
run, with every setting commented out at its default. Open the folder via
**Tray menu > Open Config Directory**.

Every setting below applies live, with no restart — including the ones that own a
global hotkey, which are re-registered in place when the file changes.

Feature switches — uncomment and set `true` to opt out:

| Flag | Default | Effect when `true` |
|---|---|---|
| `DisableSearch` | `false` | Don't register the Alt+S fuzzy-search hotkey |
| `DisableAltPan` | `false` | Disable Alt + middle-click drag to pan over windows |
| `DisableGreedyDraw` | `true` | Skip `SetWindowRgn` clipping of off-screen windows. Keeps Alt-Tab / taskbar thumbnails live at the cost of render work for windows panned out of view |
| `DisableMouseCurve` | `false` | Send raw HID deltas to the canvas (1 count = 1 pixel) instead of applying Windows' pointer-acceleration curve. Use if you've turned off "Enhance pointer precision" and want linear pan |
| `DisableZoomHotkey` | `false` | Don't register Alt+Q for the overview. The overview is still reachable by starting a pan |

Layout and navigation:

| Setting | Default | Meaning |
|---|---|---|
| `GridColumns` | `3` | Columns used by **Arrange in Grid**. Clamped to `1`–`32` |
| `AutoGridNewWindows` | `true` | Drop newly opened windows into the first free cell. Also a tray toggle |
| `ShowScreenFixedWindowsDuringPan` | `true` | Show pinned / fullscreen windows while panning. Also a tray toggle |
| `EnableDragEdgeNavigation` | `false` | Hold the left mouse button against a screen edge to advance to the next window |

### Notes

**The minimap shows app icons and pinned state.** Each window is drawn with its
application icon so you can find one at a glance, and windows pinned to the
screen are drawn red. Pinned windows stop being reprojected, so they sit still
while the canvas moves around them - showing them in red is what makes that
distinguishable from a window that is simply stuck.

**Grid cells are monitor-sized.** A window placed in a cell fills the screen while
staying an ordinary pannable canvas window — which is why you never need to
maximize. A real maximized window is excluded from the canvas entirely, and
Windows re-snaps it if anything tries to move it.

**Auto-grid only fires on an intact grid.** New windows join the layout only while
*every* window is already on a cell, so it never disturbs an arrangement you made
by hand. Set `AutoGridNewWindows=false` for on-demand gridding only.

**Dialogs never get a cell**, whether auto-placed or arranged by hand. A window is
grid-eligible only if it has no owner window and is resizable (`WS_THICKFRAME`):
a "rename branch" dialog belongs beside the window that opened it, and a
fixed-size window can't fill a cell anyway. Dialogs still live on the canvas and
pan with everything else.

**The camera only chases a window you could not already reach.** Focus arriving
from the taskbar or Alt-Tab recentres the canvas only when less than 80% of that
window is on a monitor — anything more visible is left where it is, because
moving the camera under a window you can already see reads as the view jumping
for no reason. Pinned windows are never chased.

**Edge navigation is a heuristic, not a drag detector.** A drag-and-drop running in
another application is invisible from here — `DoDragDrop` is a modal loop inside
the source process and exposes nothing — so "left button held against the edge"
is the only signal available, and that also describes selecting text to the edge
of the screen. A ~0.4s dwell keeps incidental holds from triggering it, which is
why the setting is opt-in.

## Requirements

- Windows 10/11
- .NET 8.0+
- NSIS (for installer, optional)

## Build

```bash
# C# app — the build is warning-clean, so keep it that way
dotnet build CanvasDesktop.csproj -warnaserror

# Tests (no elevation needed; everything runs against fakes)
dotnet test Tests\CanvasDesktop.Tests.csproj

# Installer (optional)
Install\build-installer.bat
```

## Author

**Devnova** — [github.com/oreyg](https://github.com/oreyg)

## License

[MIT](LICENSE)
