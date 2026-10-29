using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;
using Line = Microsoft.UI.Xaml.Shapes.Line;

namespace NksTaskMgr.WinUI;

public sealed partial class ChartView : Grid
{
    private readonly Canvas canvas = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly List<Line> horizontal = [], vertical = [];
    private readonly ShapePath areaPath = new(), linePath = new() { StrokeThickness = 1.6 }, secondaryPath = new() { StrokeThickness = 1.6, Stroke = Display.Brush(0x80c5c0) };
    private HistorySeries? series, secondary;
    private long now;
    private int seconds = 60;
    private double maximum = 100;
    private uint color = 0x80c5c0;
    private uint? paintedColor;
    private bool grid = true, filled = true;
    private readonly record struct ChartState(HistorySeries? Series, HistorySeries? Secondary, long Last, long SecondaryLast,
        int Seconds, double Width, double Height, double Maximum, uint Color, bool Grid, bool Filled);
    private ChartState? rendered;
    private long renderedAt;

    public ChartView()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        for (int i = 1; i < 5; ++i) { var line = new Line { Stroke = Display.Brush(0x2c363d), StrokeThickness = 1 }; horizontal.Add(line); canvas.Children.Add(line); }
        for (int i = 1; i < 10; ++i) { var line = new Line { Stroke = Display.Brush(0x263138), StrokeThickness = 1 }; vertical.Add(line); canvas.Children.Add(line); }
        canvas.Children.Add(areaPath); canvas.Children.Add(linePath); canvas.Children.Add(secondaryPath); Children.Add(canvas);
        SizeChanged += (_, _) => {
            for (int i = 0; i < horizontal.Count; ++i) {
                var line = horizontal[i]; line.X2 = ActualWidth; line.Y1 = line.Y2 = ActualHeight * (i + 1) / 5;
            }
            for (int i = 0; i < vertical.Count; ++i) {
                var line = vertical[i]; line.Y2 = ActualHeight; line.X1 = line.X2 = ActualWidth * (i + 1) / 10;
            }
            canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };
            Render();
        };
    }

    public void Set(HistorySeries? value, HistorySeries? upload, long time, int window, double limit, uint stroke, bool showGrid = true, bool area = true)
    {
        series = value; secondary = upload; now = time; seconds = window; maximum = limit; color = stroke; grid = showGrid; filled = area;
        Render();
    }

    private void Render()
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        var state = new ChartState(series, secondary, series?.LastTime ?? 0, secondary?.LastTime ?? 0, seconds, width, height, maximum, color, grid, filled);
        if (rendered == state && now >= renderedAt && now - renderedAt < 1_000_000_000) return;
        rendered = state; renderedAt = now;
        foreach (var line in horizontal) line.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        foreach (var line in vertical) line.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        if (series is null) { areaPath.Data = linePath.Data = secondaryPath.Data = null; return; }
        if (paintedColor != color) {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            var rgb = Windows.UI.Color.FromArgb(65, (byte)(color >> 16), (byte)(color >> 8), (byte)color);
            brush.GradientStops.Add(new GradientStop { Color = rgb, Offset = 0 });
            rgb.A = 8; brush.GradientStops.Add(new GradientStop { Color = rgb, Offset = 1 });
            areaPath.Fill = brush; linePath.Stroke = Display.Brush(color); paintedColor = color;
        }
        areaPath.Visibility = filled ? Visibility.Visible : Visibility.Collapsed;
        areaPath.Data = filled ? series.Build(width, height, now, seconds, maximum, true) : null;
        linePath.Data = series.Build(width, height, now, seconds, maximum);
        secondaryPath.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        secondaryPath.Data = secondary?.Build(width, height, now, seconds, maximum);
    }
}
