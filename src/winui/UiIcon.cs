using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace NksTaskMgr.WinUI;

public static class UiIcon
{
    public static FrameworkElement Create(bool performance, uint color, double size = 20) => Create(performance ? "performance" : "processes", color, size);
    public static FrameworkElement Create(string name, uint color, double size = 20)
    {
        var geometry = new GeometryGroup();
        void Line(params Point[] points)
        {
            var path = new PathGeometry(); var figure = new PathFigure { StartPoint = points[0], IsFilled = false };
            foreach (var point in points.Skip(1)) figure.Segments.Add(new LineSegment { Point = point });
            path.Figures.Add(figure); geometry.Children.Add(path);
        }
        if (name == "services") {
            geometry.Children.Add(new RectangleGeometry { Rect = new Rect(3, 4, 18, 6) });
            geometry.Children.Add(new RectangleGeometry { Rect = new Rect(3, 14, 18, 6) });
            Line(new Point(7, 7), new Point(8, 7)); Line(new Point(12, 7), new Point(17, 7));
            Line(new Point(7, 17), new Point(8, 17)); Line(new Point(12, 17), new Point(17, 17));
        } else if (name == "performance") {
            Line(new Point(3, 19), new Point(21, 19));
            Line(new Point(4, 15), new Point(8, 9), new Point(12, 13), new Point(16, 4), new Point(20, 10));
        } else {
            geometry.Children.Add(new RectangleGeometry { Rect = new Rect(4, 3, 16, 18) });
            Line(new Point(8, 8), new Point(16, 8)); Line(new Point(8, 12), new Point(16, 12)); Line(new Point(8, 16), new Point(13, 16));
        }
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path {
            Data = geometry, Stroke = Display.Brush(color), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
        });
        return new Viewbox { Child = canvas, Width = size, Height = size, Stretch = Stretch.Uniform };
    }
}
