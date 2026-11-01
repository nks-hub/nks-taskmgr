using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed class HistorySeries
{
    private const int Capacity = 2401;
    private long[] times = [];
    private Metric[] values = [];
    private bool[] breaks = [];
    private bool breakNext;
    private int first, count;
    public int Count => count;
    public long LastTime => count == 0 ? 0 : times[(first + count - 1) % Capacity];
    public long FirstTime => count == 0 ? 0 : times[first];
    public Metric LastValue => count == 0 ? new Metric { State = SampleState.First } : values[(first + count - 1) % Capacity];

    public void Append(long time, Metric value)
    {
        if (count > 0 && time <= LastTime) return;
        if (times.Length == 0) { times = new long[Capacity]; values = new Metric[Capacity]; breaks = new bool[Capacity]; }
        int slot = (first + count) % Capacity;
        if (count == Capacity) first = (first + 1) % Capacity;
        else ++count;
        times[slot] = time; values[slot] = value; breaks[slot] = breakNext; breakNext = false;
    }

    public void BreakNext() => breakNext = true;

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

    public (long Time, Metric Value)? At(long time)
    {
        if (count == 0 || time < FirstTime || time > LastTime + 1_250_000_000) return null;
        int low = 0, high = count - 1;
        while (low < high) {
            int middle = (low + high) / 2;
            if (times[(first + middle) % Capacity] < time) low = middle + 1; else high = middle;
        }
        int slot = (first + low) % Capacity;
        if (low > 0) {
            int previous = (slot + Capacity - 1) % Capacity;
            if (breaks[slot] && time > times[previous] && time < times[slot]) return null;
            if (Math.Abs(time - times[previous]) < Math.Abs(times[slot] - time)) slot = previous;
        }
        return Math.Abs(times[slot] - time) <= 1_250_000_000 ? (times[slot], values[slot]) : null;
    }

    public Geometry Build(double width, double height, long now, int seconds, double maximum, bool fill = false)
    {
        var geometry = new PathGeometry();
        if (width <= 0 || height <= 0 || maximum <= 0) return geometry;
        long duration = seconds * 1_000_000_000L, start = now - duration, prior = 0;
        PathFigure? figure = null;
        PolyLineSegment? line = null;
        var points = new List<Point>();
        Point last = default;
        void Close()
        {
            if (figure is null) return;
            if (fill) {
                points.Add(new Point(last.X, height));
                figure.IsClosed = true;
            }
            ReplacePoints(line!.Points, points);
            geometry.Figures.Add(figure); figure = null; line = null;
            points.Clear();
        }
        for (int i = 0; i < count; ++i) {
            int slot = (first + i) % Capacity;
            if (times[slot] < start) continue;
            var value = values[slot];
            if (breaks[slot] || value.State != SampleState.Valid || (prior > 0 && times[slot] - prior > 2_500_000_000)) Close();
            prior = times[slot];
            if (value.State != SampleState.Valid) continue;
            last = new Point(Math.Clamp((double)(times[slot] - start) / duration, 0, 1) * width,
                height * (1 - Math.Clamp(value.Value / maximum, 0, 1)));
            if (figure is null) {
                figure = new PathFigure { StartPoint = fill ? new Point(last.X, height) : last, IsFilled = fill };
                line = new PolyLineSegment(); figure.Segments.Add(line);
                if (fill) points.Add(last);
            } else points.Add(last);
        }
        Close();
        return geometry;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly struct VectorPoint(Point value)
    {
        internal readonly float X = (float)value.X, Y = (float)value.Y;
    }

    private static unsafe void ReplacePoints(PointCollection collection, List<Point> points)
    {
        // IVector<Point>::ReplaceAll is slot 17; its ABI points contain two 32-bit floats.
        var values = new VectorPoint[points.Count];
        for (int i = 0; i < values.Length; ++i) values[i] = new VectorPoint(points[i]);
        using var vector = ((WinRT.IWinRTObject)collection).NativeObject.As(ABI.System.Collections.Generic.IListMethods<Point>.IID);
        int result;
        fixed (VectorPoint* data = values) {
            var replace = (delegate* unmanaged[Stdcall]<nint, uint, VectorPoint*, int>)(*(nint**)vector.ThisPtr)[17];
            result = replace(vector.ThisPtr, (uint)values.Length, data);
        }
        GC.KeepAlive(collection);
        WinRT.ExceptionHelpers.ThrowExceptionForHR(result);
    }
}
