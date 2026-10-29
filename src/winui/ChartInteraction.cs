using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;
using Windows.Foundation;
using Windows.System;
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
        canvas.Children.Add(cursor); canvas.Children.Add(readout);
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Cross);
        PointerMoved += (_, args) => {
            if (pinned) return;
            var point = args.GetCurrentPoint(canvas).Position;
            if (point.X < 0 || point.X > canvas.ActualWidth || point.Y < 0 || point.Y > canvas.ActualHeight) { HideCursor(); return; }
            cursorTime = renderedAt - (long)((1 - point.X / Math.Max(1, canvas.ActualWidth)) * seconds * 1_000_000_000);
            UpdateCursor();
        };
        PointerExited += (_, _) => { if (!pinned && FocusState != FocusState.Keyboard) { cursorTime = null; HideCursor(); } };
        PointerPressed += (_, args) => {
            var point = args.GetCurrentPoint(canvas).Position;
            if (point.X < 0 || point.X > canvas.ActualWidth || point.Y < 0 || point.Y > canvas.ActualHeight) return;
            Focus(FocusState.Pointer);
            pinned = !pinned;
            cursorTime = renderedAt - (long)((1 - Math.Clamp(point.X / Math.Max(1, canvas.ActualWidth), 0, 1)) * seconds * 1_000_000_000);
            UpdateCursor(); args.Handled = true;
        };
        GotFocus += (_, _) => { if (FocusState == FocusState.Keyboard) { cursorTime ??= series?.LastTime; UpdateCursor(); } };
        LostFocus += (_, _) => { if (!pinned) { cursorTime = null; HideCursor(); } };
        KeyDown += OnChartKey;
    }

    private void OnChartKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { pinned = false; cursorTime = null; HideCursor(); }
        else if (args.Key == VirtualKey.Enter || args.Key == VirtualKey.Space) { pinned = !pinned; cursorTime ??= series?.LastTime; UpdateCursor(); }
        else if (args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End) {
            long start = Math.Max(series?.FirstTime ?? renderedAt, renderedAt - seconds * 1_000_000_000L), end = series?.LastTime ?? renderedAt;
            if (start > end) return;
            cursorTime = args.Key == VirtualKey.Home ? start : args.Key == VirtualKey.End ? end
                : Math.Clamp((cursorTime ?? end) + (args.Key == VirtualKey.Left ? -1 : 1) * 1_000_000_000L, start, end);
            pinned = true; UpdateCursor();
        } else return;
        args.Handled = true;
    }

    private void HideCursor() => cursor.Visibility = readout.Visibility = Visibility.Collapsed;

    private void UpdateCursor()
    {
        if (cursorTime is not { } time || canvas.ActualWidth <= 0) { HideCursor(); return; }
        double fraction = (time - renderedAt + seconds * 1_000_000_000L) / (seconds * 1_000_000_000d);
        if (fraction < 0 || fraction > 1) { cursorTime = null; pinned = false; HideCursor(); return; }
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
        reading.Text = text; AutomationProperties.SetHelpText(this, text);
        double x = fraction * canvas.ActualWidth;
        cursor.X1 = cursor.X2 = x; cursor.Y1 = 0; cursor.Y2 = canvas.ActualHeight;
        readout.MaxWidth = Math.Max(1, canvas.ActualWidth - 8);
        readout.Measure(new Size(readout.MaxWidth, double.PositiveInfinity));
        Canvas.SetLeft(readout, Math.Clamp(x + 10, 0, Math.Max(0, canvas.ActualWidth - readout.DesiredSize.Width)));
        Canvas.SetTop(readout, 4);
        cursor.Visibility = readout.Visibility = Visibility.Visible;
    }
}
