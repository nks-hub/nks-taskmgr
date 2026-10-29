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
    private HistorySeries? series, secondary;
    private long now;
    private int seconds = 60;
    private double maximum = 100;
    private uint color = 0x80c5c0;
    private bool grid = true;
    private bool filled = true;

    public ChartView()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Children.Add(canvas);
        SizeChanged += (_, _) => Render();
    }

    public void Set(HistorySeries value, HistorySeries? upload, long time, int window, double limit, uint stroke, bool showGrid = true, bool area = true)
    {
        series = value; secondary = upload; now = time; seconds = window; maximum = limit; color = stroke; grid = showGrid; filled = area;
        Render();
    }

    private void Render()
    {
        canvas.Children.Clear();
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0 || series is null) return;
        if (grid) {
            for (int i = 1; i < 5; ++i) canvas.Children.Add(new Line {
                X1 = 0, X2 = width, Y1 = height * i / 5, Y2 = height * i / 5,
                Stroke = Display.Brush(0x2c363d), StrokeThickness = 1
            });
            for (int i = 1; i < 10; ++i) canvas.Children.Add(new Line {
                X1 = width * i / 10, X2 = width * i / 10, Y1 = 0, Y2 = height,
                Stroke = Display.Brush(0x263138), StrokeThickness = 1
            });
        }
        var area = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        var rgb = Windows.UI.Color.FromArgb(65, (byte)(color >> 16), (byte)(color >> 8), (byte)color);
        area.GradientStops.Add(new GradientStop { Color = rgb, Offset = 0 });
        rgb.A = 8; area.GradientStops.Add(new GradientStop { Color = rgb, Offset = 1 });
        if (filled) canvas.Children.Add(new ShapePath { Data = series.Build(width, height, now, seconds, maximum, true), Fill = area });
        canvas.Children.Add(new ShapePath { Data = series.Build(width, height, now, seconds, maximum), Stroke = Display.Brush(color), StrokeThickness = 1.6 });
        if (secondary is not null) canvas.Children.Add(new ShapePath {
            Data = secondary.Build(width, height, now, seconds, maximum), Stroke = Display.Brush(0x80c5c0), StrokeThickness = 1.6
        });
    }

}
