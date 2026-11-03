using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static class ProcessHistoryRanking
{
    internal const int PerMetric = 8, MaximumSelected = 64;
    internal static double Score(ProcessHistorySample sample, ProcessHistoryMetric metric) => metric switch {
        ProcessHistoryMetric.Memory => sample.MemoryState == SampleState.Valid ? sample.WorkingBytes : double.NegativeInfinity,
        ProcessHistoryMetric.Io => Io(sample.ReadIo, sample.WriteIo), _ => Valid(sample.Cpu)
    };
    private static double Valid(Metric metric) => metric.State == SampleState.Valid && double.IsFinite(metric.Value) && metric.Value >= 0 ? metric.Value : double.NegativeInfinity;
    private static double Io(Metric read, Metric write)
    {
        double left = Valid(read), right = Valid(write), value = left + right;
        return double.IsFinite(value) ? value : double.NegativeInfinity;
    }
    private static double Score(ProcessRow row, ProcessHistoryMetric metric) => metric switch {
        ProcessHistoryMetric.Memory => row.Stale ? double.NegativeInfinity : row.WorkingBytes,
        ProcessHistoryMetric.Io => row.Key.Pid == 0 ? double.NegativeInfinity : Io(row.ReadIo, row.WriteIo),
        _ => row.Key.Pid == 0 ? double.NegativeInfinity : Valid(row.Cpu)
    };
    internal static ProcessRow[] Pick(IReadOnlyCollection<ProcessRow> source)
    {
        if (source.Count <= PerMetric * 3) return source.ToArray();
        var lists = new[] { new List<ProcessRow>(PerMetric), new List<ProcessRow>(PerMetric), new List<ProcessRow>(PerMetric) };
        foreach (var row in source) for (int i = 0; i < lists.Length; ++i) {
            var metric = (ProcessHistoryMetric)i; double value = Score(row, metric);
            if (!double.IsFinite(value)) continue;
            var list = lists[i];
            if (list.Count == PerMetric && value <= Score(list[^1], metric)) continue;
            int at = 0;
            while (at < list.Count && Score(list[at], metric) >= value) ++at;
            if (at >= PerMetric) continue;
            list.Insert(at, row); if (list.Count > PerMetric) list.RemoveAt(PerMetric);
        }
        return lists.SelectMany(list => list).DistinctBy(row => row.Key).ToArray();
    }
    internal static void Keep(List<ProcessHistorySample> rows, ProcessHistorySample sample, ProcessHistoryMetric metric)
    {
        double value = Score(sample, metric); if (!double.IsFinite(value)) return;
        bool verified = !sample.Key.Boot.IsEmpty && sample.Key.Generation != 0 && sample.Created100ns != 0 && sample.Key.Kind is GenerationKind.CreationTime or GenerationKind.SequenceNumber;
        int same = rows.FindIndex(row => row.Key == sample.Key && row.Created100ns == sample.Created100ns && (verified || row.TimeNs == sample.TimeNs));
        if (same >= 0) {
            if (Score(rows[same], metric) >= value) return;
            rows.RemoveAt(same);
        }
        int at = rows.FindIndex(row => Score(row, metric) < value);
        if (at < 0) at = rows.Count;
        if (at >= MaximumSelected) return;
        rows.Insert(at, sample); if (rows.Count > MaximumSelected) rows.RemoveAt(MaximumSelected);
    }
}
