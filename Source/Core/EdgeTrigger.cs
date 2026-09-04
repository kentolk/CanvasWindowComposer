namespace CanvasDesktop;

/// <summary>
/// Decides when holding the pointer against a screen edge should advance the
/// canvas — the drag-a-file-to-the-next-window gesture.
///
/// Pure state machine so the timing is testable: the caller supplies "is the
/// button down", "which edge is the cursor on" and the current time, and gets
/// back whether to navigate.
///
/// Timing matters more than it looks. There is no way to know from outside a
/// process that an OLE drag is in progress — DoDragDrop runs a modal loop in the
/// source process and exposes nothing — so "button held at the edge" is the only
/// signal available, and it also describes selecting text to the edge of the
/// screen or dragging a scrollbar. The dwell is what separates a deliberate
/// shove into the edge from an incidental one.
/// </summary>
internal sealed class EdgeTrigger
{
    private readonly long _dwellMs;
    private readonly long _repeatMs;

    private NavDirection? _edge;
    private long _since;
    private bool _fired;

    /// <param name="dwellMs">Hold time before the first advance.</param>
    /// <param name="repeatMs">Gap between subsequent advances while still held.</param>
    public EdgeTrigger(long dwellMs, long repeatMs)
    {
        _dwellMs = dwellMs;
        _repeatMs = repeatMs;
    }

    /// <summary>
    /// Advance the state machine. Returns true when the caller should navigate
    /// in <paramref name="edge"/>.
    /// </summary>
    public bool Update(bool buttonDown, NavDirection? edge, long nowMs)
    {
        if (!buttonDown || edge is null)
        {
            Reset();
            return false;
        }

        if (_edge != edge)
        {
            // Newly arrived at this edge — start the dwell, don't fire yet.
            _edge = edge;
            _since = nowMs;
            _fired = false;
            return false;
        }

        // First advance waits the full dwell; once moving, repeat on the shorter
        // interval so holding walks through windows at a readable pace.
        long threshold = _fired ? _repeatMs : _dwellMs;
        if (nowMs - _since < threshold) return false;

        _since = nowMs;
        _fired = true;
        return true;
    }

    public void Reset()
    {
        _edge = null;
        _fired = false;
    }
}
