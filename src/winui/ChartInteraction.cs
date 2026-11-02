using Microsoft.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Automation;
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

    private void OnChartKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) EffectiveTimeline.Clear();
        else if (args.Key == VirtualKey.Enter || args.Key == VirtualKey.Space) {
            long? time = cursorTime;
            if (time is null && HistoryBounds(out _, out long end)) time = end;
            if (time is null) return;
            if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0)
                EffectiveTimeline.SelectRangePoint(timelineOwner, time.Value);
            else EffectiveTimeline.SelectCursor(timelineOwner, time.Value, !pinned);
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

    private void UpdateCursor()
    {
        if (cursorTime is not { } time) {
            HideCursor(); AutomationProperties.SetHelpText(this, "Čas není vybraný. Shift+klik nebo Shift+Enter vybere dva konce intervalu."); return;
        }
        if (canvas.ActualWidth <= 0) { HideCursor(); return; }
        double fraction = (time - (double)renderedAt + seconds * 1_000_000_000d) / (seconds * 1_000_000_000d);
        if (fraction < 0 || fraction > 1) {
            HideCursor(); AutomationProperties.SetHelpText(this, "Vybraný čas " + time + " ns je mimo zobrazenou historii tohoto grafu."); return;
        }
        var sample = series?.At(time);
        string Value((long Time, Metric Value)? item) => item is null ? "Bez vzorku" : item.Value.Value.State == SampleState.Valid ? Format(item.Value.Value.Value) : Display.Metric(item.Value.Value, "");
        string text = primaryName + ": " + Value(sample);
        if (secondary is not null) text += "\n" + secondaryName + ": " + Value(secondary.At(time));
        if (overlays.Count > 0) {
            var readings = overlays.Select(item => (item.Name, Sample: item.History.At(time))).Append((primaryName, sample))
                .OrderByDescending(item => item.Item2?.Value.State == SampleState.Valid ? item.Item2.Value.Value.Value : -1).Take(5);
            text = string.Join("\n", readings.Select(item => item.Item1 + ": " + Value(item.Item2)));
            if (overlays.Count >= 5) text += "\n5 nejvytíženějších z " + (overlays.Count + 1);
        }
        text += "\n" + Display.Number(Math.Max(0, (now - (sample?.Time ?? time)) / 1_000_000_000d), " s zpět") + (pinned ? " · připnuto" : " · kliknutím připnout");
        if (sample is { } observed) text += "\nZdroj primárního vzorku: " + MeasurementText.Source(observed.Value.Source);
        text += "\nKurzor: " + time + " ns" + (sample is { } point ? " · vzorek: " + point.Time + " ns" : "");
        if (EffectiveTimeline.Interval is { } range)
            text += "\nInterval [" + range.StartNs + ", " + range.EndNs + "] ns · " + Display.Number(range.DurationNs / 1_000_000_000d, " s");
        else if (EffectiveTimeline.State.RangeAnchorNs is { } anchor)
            text += "\nPrvní konec: " + anchor + " ns · Shift+klik nebo Shift+Enter vybere druhý.";
        text += "\nČasové okno tohoto bodu není v historii uložené.";
        AutomationProperties.SetHelpText(this, text);
        double x = fraction * canvas.ActualWidth;
        cursor.X1 = cursor.X2 = x; cursor.Y1 = 0; cursor.Y2 = canvas.ActualHeight;
        cursor.Visibility = Visibility.Visible;
        if (!EffectiveTimeline.IsSource(timelineOwner) && FocusState != FocusState.Keyboard) { readout.Visibility = Visibility.Collapsed; return; }
        bool compact = canvas.ActualHeight < 140;
        reading.Text = compact ? primaryName + ": " + Value(sample)
            + (secondary is null ? "" : " · " + secondaryName + ": " + Value(secondary.At(time))) + (pinned ? " · připnuto" : "") : text;
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
