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
    private IReadOnlyList<(string Name, HistorySeries History, uint Color)> overlays = [];
    private readonly List<ShapePath> overlayPaths = [];
    private long[] renderedOverlayTimes = [];

    public void SetOverlays(IReadOnlyList<(string Name, HistorySeries History, uint Color)> values)
    {
        if (overlays.SequenceEqual(values)) return;
        overlays = values; rendered = null;
        while (overlayPaths.Count < values.Count) {
            var path = new ShapePath { StrokeThickness = 1.2, IsHitTestVisible = false };
            canvas.Children.Insert(canvas.Children.Count - 2, path); overlayPaths.Add(path);
        }
        for (int i = 0; i < overlayPaths.Count; ++i) {
            overlayPaths[i].Visibility = i < values.Count ? Visibility.Visible : Visibility.Collapsed;
            if (i < values.Count) overlayPaths[i].Stroke = Display.Brush(values[i].Color);
        }
    }

    public ChartView()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < 5; ++i) { var line = new Line { Stroke = Display.Brush(0x2c363d), StrokeThickness = 1 }; horizontal.Add(line); canvas.Children.Add(line); }
        for (int i = 1; i < 10; ++i) { var line = new Line { Stroke = Display.Brush(0x263138), StrokeThickness = 1 }; vertical.Add(line); canvas.Children.Add(line); }
        canvas.Children.Add(areaPath); canvas.Children.Add(linePath); canvas.Children.Add(secondaryPath); Children.Add(canvas);
        Grid.SetColumn(canvas, 1);
        BuildAxes(); BuildInteraction();
        Loaded += (_, _) => { Appearance.Changed += AppearanceChanged; AppearanceChanged(); };
        Unloaded += (_, _) => Appearance.Changed -= AppearanceChanged;
        canvas.SizeChanged += (_, _) => {
            for (int i = 0; i < horizontal.Count; ++i) {
                var line = horizontal[i]; line.X2 = canvas.ActualWidth; line.Y1 = line.Y2 = canvas.ActualHeight * i / 4;
            }
            for (int i = 0; i < vertical.Count; ++i) {
                var line = vertical[i]; line.Y2 = canvas.ActualHeight; line.X1 = line.X2 = canvas.ActualWidth * (i + 1) / 10;
            }
            canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight) };
            Render();
        };
    }

    private void AppearanceChanged()
    {
        paintedColor = null; rendered = null; Render();
    }

    internal static double ScaleMaximum(double observed, double capacity)
    {
        if (observed <= 0) return Math.Min(1, capacity);
        double power = Math.Pow(10, Math.Floor(Math.Log10(observed))), fraction = observed / power;
        return Math.Min(capacity, (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * power);
    }

    public void Set(HistorySeries? value, HistorySeries? upload, long time, int window, double limit, uint stroke, bool showGrid = true, bool area = true, bool autoScale = false)
    {
        if (!ReferenceEquals(series, value) || !ReferenceEquals(secondary, upload)) SeriesChanged();
        series = value; secondary = upload; now = time; seconds = Math.Max(1, window); maximum = limit; color = stroke; grid = showGrid; filled = area;
        if (autoScale) {
            long start = time - window * 1_000_000_000L;
            double observed = Math.Max(value?.Maximum(start, limit / 100) ?? 0, upload?.Maximum(start, limit / 100) ?? 0);
            foreach (var overlay in overlays) observed = Math.Max(observed, overlay.History.Maximum(start, limit / 100));
            maximum = ScaleMaximum(observed, limit);
        }
        UpdateAxes();
        Render();
    }

    private void Render()
    {
        double width = canvas.ActualWidth, height = canvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var state = new ChartState(series, secondary, series?.LastTime ?? 0, secondary?.LastTime ?? 0, seconds, width, height, maximum, color, grid, filled);
        bool overlayChanged = renderedOverlayTimes.Length != overlays.Count;
        for (int i = 0; !overlayChanged && i < overlays.Count; ++i) overlayChanged = renderedOverlayTimes[i] != overlays[i].History.LastTime;
        if (rendered == state && !overlayChanged && now >= renderedAt && now - renderedAt < 1_000_000_000) return;
        rendered = state; renderedAt = now;
        if (renderedOverlayTimes.Length != overlays.Count) renderedOverlayTimes = new long[overlays.Count];
        for (int i = 0; i < overlays.Count; ++i) renderedOverlayTimes[i] = overlays[i].History.LastTime;
        foreach (var line in horizontal) line.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        foreach (var line in vertical) line.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        if (series is null) {
            areaPath.Data = linePath.Data = secondaryPath.Data = null;
            foreach (var path in overlayPaths) path.Data = null;
            ApplyTimeline(); return;
        }
        if (paintedColor != color) {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            var rgb = ThemePalette.Color(color, 65);
            brush.GradientStops.Add(new GradientStop { Color = rgb, Offset = 0 });
            rgb.A = 8; brush.GradientStops.Add(new GradientStop { Color = rgb, Offset = 1 });
            areaPath.Fill = brush; linePath.Stroke = Display.Brush(color); paintedColor = color;
        }
        areaPath.Visibility = filled ? Visibility.Visible : Visibility.Collapsed;
        areaPath.Data = filled ? series.Build(width, height, now, seconds, maximum, true) : null;
        linePath.Data = series.Build(width, height, now, seconds, maximum);
        secondaryPath.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        secondaryPath.Data = secondary?.Build(width, height, now, seconds, maximum);
        for (int i = 0; i < overlays.Count; ++i) overlayPaths[i].Data = overlays[i].History.Build(width, height, now, seconds, maximum);
        ApplyTimeline();
    }
}
