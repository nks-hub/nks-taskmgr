using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static void HistoryRankingSelection()
    {
        ProcessRow Row(uint pid, double cpu, ulong memory, double read, double write, bool stale = false)
        {
            var row = new ProcessRow(new(new(1, 2), pid, GenerationKind.CreationTime, pid + 1));
            row.Update(new ProcessInfo {
                CpuPercent = new Metric { Value = cpu, State = SampleState.Valid }, WorkingSetBytes = memory,
                ReadIoBytesPerSecond = new Metric { Value = read, State = SampleState.Valid },
                WriteIoBytesPerSecond = new Metric { Value = write, State = SampleState.Valid }
            }, false);
            row.SetStatus(false, stale, false);
            return row;
        }

        double Value(Metric metric) => metric.State == SampleState.Valid && double.IsFinite(metric.Value) && metric.Value >= 0
            ? metric.Value : double.NegativeInfinity;
        double Cpu(ProcessRow row) => row.Key.Pid == 0 ? double.NegativeInfinity : Value(row.Cpu);
        double Memory(ProcessRow row) => row.Stale ? double.NegativeInfinity : row.WorkingBytes;
        double Io(ProcessRow row) => row.Key.Pid == 0 ? double.NegativeInfinity : Value(row.ReadIo) + Value(row.WriteIo);

        void Check(ProcessRow[] rows)
        {
            var expected = rows.Length <= 24 ? rows : new[] { (Func<ProcessRow, double>)Cpu, Memory, Io }
                .SelectMany(score => rows.Where(row => double.IsFinite(score(row))).OrderByDescending(score).Take(8))
                .DistinctBy(row => row.Key).ToArray();
            var actual = ProcessHistoryRanking.Pick(rows);
            Require(actual.SequenceEqual(expected, ReferenceEqualityComparer.Instance),
                "History ranking changed a selected object, stable tie or metric ordering.");
        }

        Check([]);
        var ties = Enumerable.Range(0, 500).Select(i => Row((uint)i, i % 4, (ulong)(i % 5), i % 3, i % 3)).ToArray();
        Check(ties[..24]); Check(ties[..25]); Check(ties); Check(ties.Reverse().ToArray());
        var edges = new[] {
            Row(0, 1000, ulong.MaxValue, 1000, 1000), Row(1, double.NaN, 1, 0, 1),
            Row(2, double.PositiveInfinity, 1, double.MaxValue, double.MaxValue), Row(3, -1, 1, -1, 1),
            Row(4, 100, ulong.MaxValue, 100, 100, true), Row(5, 0, 1UL << 53, 0, 0),
            Row(6, 0, (1UL << 53) + 1, 0, 0), Row(7, 100, 100, 100, 100), Row(7, 200, 200, 200, 200)
        };
        edges[1].Update(new ProcessInfo { CpuPercent = new Metric { Value = 1000, State = SampleState.First },
            WorkingSetBytes = 1, ReadIoBytesPerSecond = new Metric { Value = 1000, State = SampleState.Unavailable },
            WriteIoBytesPerSecond = new Metric { Value = 1000, State = SampleState.Valid } }, false);
        Check(edges.Concat(ties).ToArray()); Check(ties.Concat(edges).ToArray());
        var store = new ProcessHistoryStore(); var steady = Row(10, 0, 0, 0, 0); var other = Row(11, 5, 50, 0, 0);
        steady.SetCreatedTime(1000); other.SetCreatedTime(1100);
        for (int i = 1; i <= 3; ++i) {
            steady.Update(new ProcessInfo { CpuPercent = new Metric { Value = i * 10, State = SampleState.Valid }, WorkingSetBytes = (ulong)i * 100,
                ReadIoBytesPerSecond = new Metric { State = SampleState.Unavailable }, WriteIoBytesPerSecond = new Metric { State = SampleState.Unavailable } }, false);
            store.Capture(i * 1_000_000_000L, 0, (ulong)i, i == 2 ? [steady, other] : [steady], null, null);
        }
        var range = store.Select(1_000_000_000L, 3_000_000_000L);
        var summary = range.Summaries.Single(row => row.Last.Key == steady.Key);
        Require(range.FrameCount == 3 && range.Summaries.Count == 2 && range.CpuRows.Count == 0 && summary.Samples == 3
            && summary.Average(ProcessHistoryMetric.Cpu) == 20 && summary.Peak(ProcessHistoryMetric.Cpu) == 30 && summary.Total(ProcessHistoryMetric.Cpu) == 60
            && summary.Peak(ProcessHistoryMetric.Memory) == 300 && double.IsNaN(summary.Average(ProcessHistoryMetric.Io))
            && range.Summaries.Single(row => row.Last.Key == other.Key).Samples == 1, "Interval process summary miscounted samples, average, peak or invalid I/O.");
        var system = new HistorySeries();
        system.Append(1, new Metric { Value = 10, State = SampleState.Valid }); system.Append(2, new Metric { Value = 90, State = SampleState.Unavailable });
        system.Append(3, new Metric { Value = 30, State = SampleState.Valid }); system.Append(4, new Metric { Value = 50, State = SampleState.Valid });
        Require(system.Summary(1, 3) == (20, 30) && system.Summary(5, 9) is null, "System range summary used an invalid or out-of-range sample.");
        App.Trace("History ranking passed: small snapshots, stable ties, input order, idle PID, stale rows, invalid metrics, large memory and duplicate keys");
    }
}
