using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Maps windows to icon-atlas slots for the minimap.
///
/// Two levels of caching, both load-bearing. Slots are allocated per *process*
/// because every window of an application shares its icon, and reading one costs
/// a process lookup plus GDI rasterisation. Windows are then cached to their
/// process's slot, because the minimap rebuilds its snapshot on every camera
/// change — once per input frame during a pan — and a P/Invoke per window there
/// would sit directly on the pan path.
///
/// A failed read is cached as -1 rather than retried: a protected or exited
/// process, or an exe with no icon resource, would otherwise be re-probed
/// forever.
/// </summary>
internal sealed class IconSlotCache
{
    private readonly IWindowApi _win32;
    private readonly int _capacity;
    private readonly int _iconPx;
    private readonly Action<int, byte[]> _upload;

    private readonly Dictionary<uint, int> _slotByPid = new();
    private readonly Dictionary<IntPtr, int> _slotByWindow = new();
    private int _nextSlot;

    /// <param name="upload">Hands the pixels to the renderer for a newly claimed slot.</param>
    public IconSlotCache(IWindowApi win32, int capacity, int iconPx, Action<int, byte[]> upload)
    {
        _win32 = win32;
        _capacity = capacity;
        _iconPx = iconPx;
        _upload = upload;
    }

    /// <summary>Slot holding this window's application icon, or -1 if there is none.</summary>
    public int SlotFor(IntPtr hWnd)
    {
        if (_slotByWindow.TryGetValue(hWnd, out int cached))
            return cached;

        int slot = SlotForProcess(_win32.GetWindowProcessId(hWnd));
        _slotByWindow[hWnd] = slot;
        return slot;
    }

    /// <summary>Drop a closed window. Its process keeps its slot for other windows.</summary>
    public void Forget(IntPtr hWnd)
    {
        _slotByWindow.Remove(hWnd);
    }

    private int SlotForProcess(uint pid)
    {
        if (_slotByPid.TryGetValue(pid, out int known))
            return known;

        int slot = -1;
        if (_nextSlot < _capacity)
        {
            byte[]? pixels = _win32.GetProcessIconBgra(pid, _iconPx);
            if (pixels != null)
            {
                slot = _nextSlot++;
                _upload(slot, pixels);
            }
        }

        _slotByPid[pid] = slot;
        return slot;
    }
}
