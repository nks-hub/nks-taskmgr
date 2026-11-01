namespace NksTaskMgr.WinUI;

public readonly record struct ChartTimelineRange(long StartNs, long EndNs)
{
    public long DurationNs => EndNs - StartNs;
}

public readonly record struct ChartTimelineState(long? CursorTimeNs, bool IsPinned, long? RangeAnchorNs, long? RangeEndNs)
{
    internal object? SourceOwner { get; init; }
    public ChartTimelineRange? Interval => RangeAnchorNs is { } a && RangeEndNs is { } b && a != b
        ? new(Math.Min(a, b), Math.Max(a, b)) : null;
}

public sealed class ChartTimelineChangedEventArgs(ChartTimelineState state) : EventArgs
{
    public ChartTimelineState State { get; } = state;
}

// A timeline belongs to its views' UI thread. It never queries a current process or a clock.
public sealed class SharedChartTimeline
{
    private object? hoverOwner;
    public ChartTimelineState State { get; private set; }
    public ChartTimelineRange? Interval => State.Interval;
    public event EventHandler<ChartTimelineChangedEventArgs>? Changed;
    public event EventHandler<ChartTimelineChangedEventArgs>? RangeChanged;

    public void SetCursor(long? timeNs, bool pinned = false)
    {
        Validate(timeNs); hoverOwner = null;
        Publish(new(timeNs, timeNs is not null && pinned, null, null));
    }
    public void SetRange(long startNs, long endNs)
    {
        Validate(startNs); Validate(endNs);
        if (startNs == endNs) throw new ArgumentException("Konce intervalu musí být různé.");
        hoverOwner = null; Publish(new(endNs, true, startNs, endNs));
    }
    public void Clear() { hoverOwner = null; Publish(default); }
    public void ClearRange() => Publish(State with { RangeAnchorNs = null, RangeEndNs = null });

    internal void Hover(object owner, long timeNs)
    {
        Validate(timeNs);
        if (State.IsPinned) return;
        hoverOwner = owner; Publish(State with { CursorTimeNs = timeNs, SourceOwner = owner });
    }
    internal void Leave(object owner)
    {
        if (!ReferenceEquals(hoverOwner, owner)) return;
        hoverOwner = null;
        if (!State.IsPinned) Publish(State with { CursorTimeNs = null, SourceOwner = null });
    }
    internal bool IsSource(object owner) => ReferenceEquals(State.SourceOwner, owner);
    internal void Focus(object owner) => Publish(State with { SourceOwner = owner });
    internal void SelectCursor(object owner, long timeNs, bool pinned)
    {
        Validate(timeNs); hoverOwner = pinned ? null : owner;
        Publish(new(timeNs, pinned, null, null) { SourceOwner = owner });
    }
    internal void Navigate(object owner, long timeNs)
    {
        Validate(timeNs); hoverOwner = null;
        // The first endpoint survives keyboard navigation until Shift+Enter picks the second.
        Publish(new(timeNs, true, State.RangeEndNs is null ? State.RangeAnchorNs : null, null) { SourceOwner = owner });
    }
    internal void SelectRangePoint(object owner, long timeNs)
    {
        Validate(timeNs); hoverOwner = null;
        if (State.RangeAnchorNs is not { } start || State.RangeEndNs is not null)
            Publish(new(timeNs, true, timeNs, null) { SourceOwner = owner });
        else Publish(new(timeNs, true, start, timeNs == start ? null : timeNs) { SourceOwner = owner });
    }
    private void Publish(ChartTimelineState value)
    {
        if (value == State) return;
        bool rangeChanged = value.RangeAnchorNs != State.RangeAnchorNs || value.RangeEndNs != State.RangeEndNs;
        State = value;
        var args = new ChartTimelineChangedEventArgs(value);
        Changed?.Invoke(this, args);
        if (rangeChanged) RangeChanged?.Invoke(this, args);
    }
    private static void Validate(long? timeNs)
    {
        if (timeNs < 0) throw new ArgumentOutOfRangeException(nameof(timeNs));
    }
}
