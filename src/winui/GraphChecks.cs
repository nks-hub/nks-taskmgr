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
        var measured = new HistorySeries();
        measured.Append(1_000_000_000, new Metric { Value = 0, State = SampleState.Valid });
        measured.Append(2_000_000_000, new Metric { Value = 50, State = SampleState.Valid });
        measured.Append(3_000_000_000, new Metric { Value = 100, State = SampleState.Valid });
        var measuredLine = measured.Build(100, 80, 5_000_000_000, 5, 100).As<PathGeometry>();
        var measuredFigure = measuredLine.Figures[0];
        var measuredPoints = measuredFigure.Segments[0].As<PolyLineSegment>().Points;
        Require(measuredLine.Figures.Count == 1 && !measuredFigure.IsClosed && !measuredFigure.IsFilled
            && measuredFigure.StartPoint.X == 20 && measuredFigure.StartPoint.Y == 80 && measuredPoints.Count == 2
            && measuredPoints[0].X == 40 && measuredPoints[0].Y == 40 && measuredPoints[1].X == 60 && measuredPoints[1].Y == 0,
            "Graph stroke lost a valid zero or changed the ordered measured points.");
        var measuredArea = measured.Build(100, 80, 5_000_000_000, 5, 100, true).As<PathGeometry>();
        var measuredFill = measuredArea.Figures[0];
        var fillPoints = measuredFill.Segments[0].As<PolyLineSegment>().Points;
        Require(measuredArea.Figures.Count == 1 && measuredFill.IsClosed && measuredFill.IsFilled
            && measuredFill.StartPoint.X == 20 && measuredFill.StartPoint.Y == 80 && fillPoints.Count == 4
            && fillPoints[0].X == 20 && fillPoints[0].Y == 80 && fillPoints[1].X == 40 && fillPoints[1].Y == 40
            && fillPoints[2].X == 60 && fillPoints[2].Y == 0 && fillPoints[3].X == 60 && fillPoints[3].Y == 80,
            "Graph fill omitted a measured endpoint or changed its closing baseline.");

        var sparse = new HistorySeries();
        sparse.Append(1_000_000_000, new Metric { Value = 25, State = SampleState.Valid });
        sparse.Append(3_500_000_000, new Metric { Value = 50, State = SampleState.Valid });
        sparse.Append(6_000_000_001, new Metric { Value = 75, State = SampleState.Valid });
        sparse.BreakNext();
        sparse.Append(7_000_000_000, new Metric { Value = 0, State = SampleState.Valid });
        var sparseLine = sparse.Build(80, 100, 8_000_000_000, 8, 100).As<PathGeometry>();
        Require(sparseLine.Figures.Count == 3 && sparseLine.Figures[0].Segments[0].As<PolyLineSegment>().Points.Count == 1
            && sparseLine.Figures[1].Segments[0].As<PolyLineSegment>().Points.Count == 0
            && sparseLine.Figures[2].Segments[0].As<PolyLineSegment>().Points.Count == 0
            && sparseLine.Figures[2].StartPoint.X == 70 && sparseLine.Figures[2].StartPoint.Y == 100,
            "Graph changed the 2.5 s gap boundary, explicit break, or isolated zero stroke.");
        var sparseArea = sparse.Build(80, 100, 8_000_000_000, 8, 100, true).As<PathGeometry>();
        Require(sparseArea.Figures.Count == 3 && sparseArea.Figures[0].Segments[0].As<PolyLineSegment>().Points.Count == 3,
            "Sparse graph fill changed the connected segment at the gap boundary.");
        for (int i = 1; i < sparseArea.Figures.Count; ++i) {
            var isolated = sparseArea.Figures[i];
            var isolatedPoints = isolated.Segments[0].As<PolyLineSegment>().Points;
            Require(isolated.IsClosed && isolated.IsFilled && isolatedPoints.Count == 2
                && isolated.StartPoint.Y == 100 && isolated.StartPoint.X == isolatedPoints[0].X
                && isolatedPoints[1].X == isolatedPoints[0].X && isolatedPoints[1].Y == 100,
                "An isolated graph fill inherited another segment's points or missed its own baseline.");
        }
        Require(history.At(500_000_000) is null && history.At(6_000_000_000) is null, "Graph cursor invented data before history or inside a sampling gap.");
        Require(history.At(3_000_000_000)?.Value.State == SampleState.Unavailable
            && history.At(1_900_000_000)?.Value.Value == 20, "Graph cursor lost invalid samples or selected a different measured value.");
        Require(ChartView.ScaleMaximum(7.3, 100) == 10 && ChartView.ScaleMaximum(57, 100) == 100
            && ChartView.ScaleMaximum(140, 100) == 100, "Automatic graph scale hid a peak or exceeded physical capacity.");
        var miniature = new HistorySeries();
        miniature.Append(1_000_000_000, new Metric { Value = 0, State = SampleState.Valid });
        miniature.Append(2_000_000_000, new Metric { Value = 100, State = SampleState.Valid });
        var mini = miniature.Build(64, 38, 2_000_000_000, 2, 100).As<PathGeometry>();
        var miniPoints = mini.Figures[0].Segments[0].As<PolyLineSegment>().Points;
        Require(mini.Figures.Count == 1 && mini.Figures[0].StartPoint.X == 32 && mini.Figures[0].StartPoint.Y == 38
            && miniPoints.Count == 1 && miniPoints[0].X == 64 && miniPoints[0].Y == 0,
            "The 64x38 resource preview changed valid endpoints or its fixed percent scale.");
        App.Trace("Graph polylines passed: ordered points, valid zero, sparse and invalid gaps, closed fills");
    }
}
