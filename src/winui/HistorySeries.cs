using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed class HistorySeries
{
    private const int Capacity = 2401;
    private readonly long[] times = new long[Capacity];
    private readonly Metric[] values = new Metric[Capacity];
    private int first, count;
    public int Count => count;
    public long LastTime => count == 0 ? 0 : times[(first + count - 1) % Capacity];
    public long FirstTime => count == 0 ? 0 : times[first];
    public Metric LastValue => count == 0 ? new Metric { State = SampleState.First } : values[(first + count - 1) % Capacity];

    public void Append(long time, Metric value)
    {
        if (count > 0 && time <= LastTime) return;
        int slot = (first + count) % Capacity;
        if (count == Capacity) first = (first + 1) % Capacity;
        else ++count;
        times[slot] = time; values[slot] = value;
    }

    public double Maximum(long start, double minimum)
    {
        double maximum = minimum;
        for (int i = 0; i < count; ++i) {
            int slot = (first + i) % Capacity;
            if (times[slot] >= start && values[slot].State == SampleState.Valid)
                maximum = Math.Max(maximum, values[slot].Value * 1.1);
        }
        return maximum;
    }

    public Geometry Build(double width, double height, long now, int seconds, double maximum, bool fill = false)
    {
        var geometry = new PathGeometry();
        if (width <= 0 || height <= 0 || maximum <= 0) return geometry;
        long duration = seconds * 1_000_000_000L, start = now - duration, prior = 0;
        PathFigure? figure = null;
        Point last = default;
        void Close()
        {
            if (figure is null) return;
            if (fill) {
                figure.Segments.Add(new LineSegment { Point = new Point(last.X, height) });
                figure.IsClosed = true;
            }
            geometry.Figures.Add(figure); figure = null;
        }
        for (int i = 0; i < count; ++i) {
            int slot = (first + i) % Capacity;
            if (times[slot] < start) continue;
            var value = values[slot];
            if (value.State != SampleState.Valid || (prior > 0 && times[slot] - prior > 2_500_000_000)) Close();
            prior = times[slot];
            if (value.State != SampleState.Valid) continue;
            last = new Point(Math.Clamp((double)(times[slot] - start) / duration, 0, 1) * width,
                height * (1 - Math.Clamp(value.Value / maximum, 0, 1)));
            if (figure is null) {
                figure = new PathFigure { StartPoint = fill ? new Point(last.X, height) : last, IsFilled = fill };
                if (fill) figure.Segments.Add(new LineSegment { Point = last });
            } else figure.Segments.Add(new LineSegment { Point = last });
        }
        Close();
        return geometry;
    }
}
