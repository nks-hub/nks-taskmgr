using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;
using WinRT;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static void Graphs()
    {
        var history = new HistorySeries();
        void Sample(long seconds, double value, SampleState state = SampleState.Valid) =>
            history.Append(seconds * 1_000_000_000, new Metric { Value = value, State = state });
        Sample(1, 10); Sample(2, 20); Sample(3, 0, SampleState.Unavailable); Sample(4, 30); Sample(8, 40);
        var stroke = history.Build(100, 100, 8_000_000_000, 10, 100).As<PathGeometry>();
        Require(stroke.Figures.Count == 3, "GPU polyline connected invalid or missing samples.");
        var first = stroke.Figures[0]; var points = first.Segments[0].As<PolyLineSegment>().Points;
        Require(first.Segments.Count == 1 && points.Count == 1 && Math.Abs(first.StartPoint.X - 30) < .001
            && first.StartPoint.Y == 90 && points[0].X == 40 && points[0].Y == 80, "GPU polyline changed the measured coordinates.");
        var area = history.Build(100, 100, 8_000_000_000, 10, 100, true).As<PathGeometry>();
        foreach (var figure in area.Figures) {
            var path = figure.Segments[0].As<PolyLineSegment>().Points;
            Require(figure.IsClosed && figure.IsFilled && figure.StartPoint.Y == 100 && path[path.Count - 1].Y == 100,
                "GPU fill crossed a gap or missed the baseline.");
        }
        App.Trace("Graph polylines passed: measured coordinates, gaps and closed fills");
    }
}
