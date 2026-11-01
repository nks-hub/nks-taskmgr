using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Line = Microsoft.UI.Xaml.Shapes.Line;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace NksTaskMgr.WinUI;

public sealed partial class ChartView
{
    private readonly SharedChartTimeline localTimeline = new();
    // The timeline retains only this token, never a chart through its hover owner.
    private readonly object timelineOwner = new();
    private SharedChartTimeline? sharedTimeline, subscribedTimeline;
    private bool timelineLoaded;
    private readonly Rectangle intervalBand = new() {
        Fill = new SolidColorBrush(ThemePalette.Color(0x80c5c0, 32)), IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };
    private readonly Line intervalStart = IntervalLine(), intervalEnd = IntervalLine();

    public SharedChartTimeline? Timeline {
        get => sharedTimeline;
        set {
            if (ReferenceEquals(sharedTimeline, value)) return;
            DetachTimeline(); sharedTimeline = value;
            AttachTimeline(); ApplyTimeline();
        }
    }
    private SharedChartTimeline EffectiveTimeline => sharedTimeline ?? localTimeline;
    private static Line IntervalLine() => new() {
        Stroke = Display.Brush(0x80c5c0), StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };
    private void BuildTimeline()
    {
        canvas.Children.Add(intervalBand); canvas.Children.Add(intervalStart); canvas.Children.Add(intervalEnd);
        Loaded += (_, _) => { timelineLoaded = true; AttachTimeline(); ApplyTimeline(); };
        Unloaded += (_, _) => { timelineLoaded = false; DetachTimeline(); HideCursor(); HideInterval(); };
    }
    private void AttachTimeline()
    {
        if (!timelineLoaded || subscribedTimeline is not null) return;
        subscribedTimeline = EffectiveTimeline; subscribedTimeline.Changed += TimelineChanged;
    }
    private void DetachTimeline()
    {
        if (subscribedTimeline is not { } previous) return;
        previous.Changed -= TimelineChanged; subscribedTimeline = null;
        previous.Leave(timelineOwner);
    }
    private void TimelineChanged(object? sender, ChartTimelineChangedEventArgs args) => ApplyTimeline();
    private void ApplyTimeline()
    {
        var state = EffectiveTimeline.State;
        cursorTime = state.CursorTimeNs; pinned = state.IsPinned;
        UpdateCursor(); UpdateInterval();
    }
    private void SeriesChanged()
    {
        if (sharedTimeline is null) localTimeline.Clear();
        else EffectiveTimeline.Leave(timelineOwner);
        ApplyTimeline();
    }
    private long TimeAt(double x) => Math.Max(0, renderedAt
        - (long)((1 - Math.Clamp(x / Math.Max(1, canvas.ActualWidth), 0, 1)) * seconds * 1_000_000_000d));
    private bool InPlot(Point point) => point.X >= 0 && point.X <= canvas.ActualWidth
        && point.Y >= 0 && point.Y <= canvas.ActualHeight && canvas.ActualWidth > 0 && canvas.ActualHeight > 0;
    private bool HistoryBounds(out long start, out long end)
    {
        long first = long.MaxValue, last = 0;
        void Include(HistorySeries? value) {
            if (value is null || value.Count == 0) return;
            first = Math.Min(first, value.FirstTime); last = Math.Max(last, value.LastTime);
        }
        Include(series); Include(secondary);
        foreach (var overlay in overlays) Include(overlay.History);
        start = Math.Max(first, Math.Max(0, renderedAt - seconds * 1_000_000_000L)); end = Math.Min(last, renderedAt);
        return start <= end;
    }
    private void HideInterval() => intervalBand.Visibility = intervalStart.Visibility = intervalEnd.Visibility = Visibility.Collapsed;
    private void UpdateInterval()
    {
        HideInterval();
        if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
        var state = EffectiveTimeline.State;
        if (state.RangeAnchorNs is not { } anchor) return;
        double Fraction(long time) => (time - renderedAt + seconds * 1_000_000_000d) / (seconds * 1_000_000_000d);
        void Boundary(Line line, long time) {
            double fraction = Fraction(time);
            if (fraction < 0 || fraction > 1) return;
            line.X1 = line.X2 = fraction * canvas.ActualWidth; line.Y1 = 0; line.Y2 = canvas.ActualHeight;
            line.Visibility = Visibility.Visible;
        }
        if (state.Interval is not { } range) { Boundary(intervalStart, anchor); return; }
        Boundary(intervalStart, range.StartNs); Boundary(intervalEnd, range.EndNs);
        double left = Math.Clamp(Fraction(range.StartNs), 0, 1), right = Math.Clamp(Fraction(range.EndNs), 0, 1);
        if (right <= left) return;
        Canvas.SetLeft(intervalBand, left * canvas.ActualWidth); Canvas.SetTop(intervalBand, 0);
        intervalBand.Width = (right - left) * canvas.ActualWidth; intervalBand.Height = canvas.ActualHeight;
        intervalBand.Visibility = Visibility.Visible;
    }
}
