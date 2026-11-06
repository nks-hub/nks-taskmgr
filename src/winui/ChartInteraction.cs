using Microsoft.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Line = Microsoft.UI.Xaml.Shapes.Line;

namespace NksTaskMgr.WinUI;

public sealed partial class ChartView
{
    private readonly Line cursor = new() { Stroke = Display.Brush(0xe9eef0), StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly TextBlock reading = Caption();
    private readonly Border readout = new() { Background = Display.Brush(0x12181c), BorderBrush = Display.Brush(0x52616a), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 5, 8, 5), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private long? cursorTime;
    private bool pinned;

    private void BuildInteraction()
    {
        reading.Foreground = Display.Brush(0xe9eef0); reading.TextWrapping = TextWrapping.Wrap;
        readout.Child = reading;
        BuildTimeline();
        canvas.Children.Add(cursor); canvas.Children.Add(readout);
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Cross);
        PointerMoved += (_, args) => {
            if (pinned) return;
            var point = args.GetCurrentPoint(canvas).Position;
            if (!InPlot(point)) { EffectiveTimeline.Leave(timelineOwner); return; }
            EffectiveTimeline.Hover(timelineOwner, TimeAt(point.X));
        };
        PointerExited += (_, _) => { if (FocusState != FocusState.Keyboard) EffectiveTimeline.Leave(timelineOwner); };
        PointerPressed += (_, args) => {
            var point = args.GetCurrentPoint(canvas).Position;
            var buttons = args.GetCurrentPoint(canvas).Properties;
            if (!InPlot(point) || buttons.IsRightButtonPressed || buttons.IsMiddleButtonPressed) return;
            Focus(FocusState.Pointer);
            long time = TimeAt(point.X);
            if ((args.KeyModifiers & VirtualKeyModifiers.Shift) != 0) EffectiveTimeline.SelectRangePoint(timelineOwner, time);
            else EffectiveTimeline.SelectCursor(timelineOwner, time, !pinned);
            args.Handled = true;
        };
        GotFocus += (_, _) => {
            if (FocusState != FocusState.Keyboard) return;
            if (cursorTime is not null) EffectiveTimeline.Focus(timelineOwner);
            else if (HistoryBounds(out _, out long end)) EffectiveTimeline.Hover(timelineOwner, end);
        };
        LostFocus += (_, _) => EffectiveTimeline.Leave(timelineOwner);
        KeyDown += OnChartKey;
    }

    /// <summary>Pins (or unpins) the hovered time, or the newest sample when nothing is hovered; with range it marks one end of an interval.</summary>
    private bool Activate(bool range)
    {
        long? time = cursorTime;
        if (time is null && HistoryBounds(out _, out long end)) time = end;
        if (time is null) return false;
        if (range) EffectiveTimeline.SelectRangePoint(timelineOwner, time.Value);
        else EffectiveTimeline.SelectCursor(timelineOwner, time.Value, !pinned);
        return true;
    }

    // A Grid has no automation peer, so without this the chart and its name were invisible to screen readers and UI Automation.
    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this);

    private sealed partial class ChartAutomationPeer(ChartView owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override string GetClassNameCore() => nameof(ChartView);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override bool IsKeyboardFocusableCore() => owner.IsTabStop;
        protected override object? GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.Invoke && owner.IsTabStop ? this : base.GetPatternCore(pattern);
        public void Invoke() { if (owner.IsTabStop) owner.Activate(false); }
    }

    private void OnChartKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) EffectiveTimeline.Clear();
        else if (args.Key == VirtualKey.Enter || args.Key == VirtualKey.Space) {
            if (!Activate((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0)) return;
        }
        else if (args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End) {
            if (!HistoryBounds(out long start, out long end)) return;
            long current = Math.Clamp(cursorTime ?? end, start, end);
            long time = args.Key == VirtualKey.Home ? start : args.Key == VirtualKey.End ? end : args.Key == VirtualKey.Left
                ? current - Math.Min(1_000_000_000L, current - start) : current + Math.Min(1_000_000_000L, end - current);
            EffectiveTimeline.Navigate(timelineOwner, time);
        } else return;
        args.Handled = true;
    }

    private void HideCursor() => cursor.Visibility = readout.Visibility = Visibility.Collapsed;

    // Chart times are monotonic; the readout shows the matching local clock time.
    private static string Clock(long monotonicNs)
        => DateTime.Now.AddTicks(-(NativeMonitor.MonotonicNowNs - monotonicNs) / 100).ToString("HH:mm:ss.fff");

    private void UpdateCursor()
    {
        if (cursorTime is not { } time) {
            HideCursor(); AutomationProperties.SetHelpText(this, Loc.T("Čas není vybraný. Shift+klik nebo Shift+Enter vybere dva konce intervalu.")); return;
        }
        if (canvas.ActualWidth <= 0) { HideCursor(); return; }
        double fraction = (time - (double)renderedAt + seconds * 1_000_000_000d) / (seconds * 1_000_000_000d);
        if (fraction < 0 || fraction > 1) {
            HideCursor(); AutomationProperties.SetHelpText(this, Loc.F("Vybraný čas {0} je mimo zobrazenou historii tohoto grafu.", Clock(time))); return;
        }
        var sample = series?.At(time);
        string Value((long Time, Metric Value)? item) => item is null ? Loc.T("Bez vzorku") : item.Value.Value.State == SampleState.Valid ? Format(item.Value.Value.Value) : Display.Metric(item.Value.Value, "");
        string text = primaryName + ": " + Value(sample);
        if (secondary is not null) text += "\n" + secondaryName + ": " + Value(secondary.At(time));
        if (overlays.Count > 0) {
            var readings = overlays.Select(item => (item.Name, Sample: item.History.At(time))).Append((primaryName, sample))
                .OrderByDescending(item => item.Item2?.Value.State == SampleState.Valid ? item.Item2.Value.Value.Value : -1).Take(5);
            text = string.Join("\n", readings.Select(item => item.Item1 + ": " + Value(item.Item2)));
            if (overlays.Count >= 5) text += "\n" + Loc.F("5 nejvytíženějších z {0}", overlays.Count + 1);
        }
        text += "\n" + Loc.F("{0} s zpět", Display.Number(Math.Max(0, (now - (sample?.Time ?? time)) / 1_000_000_000d), "")) + " · " + (pinned ? Loc.T("připnuto") : Loc.T("kliknutím připnout"));
        if (sample is { } observed) text += "\n" + Loc.F("Zdroj primárního vzorku: {0}", MeasurementText.Source(observed.Value.Source));
        text += "\n" + Loc.F("Čas: {0}", Clock(sample?.Time ?? time));
        if (EffectiveTimeline.Interval is { } range)
            text += "\n" + Loc.F("Úsek: {0} – {1} · {2}", Clock(range.StartNs), Clock(range.EndNs), Display.Number(range.DurationNs / 1_000_000_000d, " s"));
        else if (EffectiveTimeline.State.RangeAnchorNs is { } anchor)
            text += "\n" + Loc.F("První konec: {0} · Shift+klik nebo Shift+Enter vybere druhý.", Clock(anchor));
        AutomationProperties.SetHelpText(this, text);
        double x = fraction * canvas.ActualWidth;
        cursor.X1 = cursor.X2 = x; cursor.Y1 = 0; cursor.Y2 = canvas.ActualHeight;
        cursor.Visibility = Visibility.Visible;
        if (!EffectiveTimeline.IsSource(timelineOwner) && FocusState != FocusState.Keyboard) { readout.Visibility = Visibility.Collapsed; return; }
        bool compact = canvas.ActualHeight < 140;
        reading.Text = compact ? primaryName + ": " + Value(sample)
            + (secondary is null ? "" : " · " + secondaryName + ": " + Value(secondary.At(time))) + (pinned ? " · " + Loc.T("připnuto") : "") : text;
        reading.MaxLines = compact ? secondary is null ? 1 : 2 : 6; reading.FontSize = 11;
        reading.TextTrimming = TextTrimming.CharacterEllipsis;
        readout.Padding = compact ? new Thickness(4, 1, 4, 1) : new Thickness(8, 5, 8, 5);
        readout.MaxHeight = compact ? Math.Max(1, canvas.ActualHeight / (secondary is null ? 3 : 2)) : Math.Max(40, canvas.ActualHeight * .65);
        readout.MaxWidth = Math.Max(1, canvas.ActualWidth - 8);
        readout.Measure(new Size(readout.MaxWidth, double.PositiveInfinity));
        Canvas.SetLeft(readout, Math.Clamp(x + 10, 0, Math.Max(0, canvas.ActualWidth - readout.DesiredSize.Width)));
        Canvas.SetTop(readout, 4);
        readout.Visibility = Visibility.Visible;
    }
}
