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
        double Network(ProcessRow row) => row.Stale || row.NetworkRate is not { } rate ? double.NegativeInfinity : rate.PerSecond;

        void Check(ProcessRow[] rows)
        {
            var expected = rows.Length <= 32 ? rows : new[] { (Func<ProcessRow, double>)Cpu, Memory, Io, Network }
                .SelectMany(score => rows.Where(row => double.IsFinite(score(row))).OrderByDescending(score).Take(8))
                .DistinctBy(row => row.Key).ToArray();
            var actual = ProcessHistoryRanking.Pick(rows);
            Require(actual.SequenceEqual(expected, ReferenceEqualityComparer.Instance),
                "History ranking changed a selected object, stable tie or metric ordering.");
        }

        Check([]);
        var ties = Enumerable.Range(0, 500).Select(i => Row((uint)i, i % 4, (ulong)(i % 5), i % 3, i % 3)).ToArray();
        Check(ties[..32]); Check(ties[..33]); Check(ties); Check(ties.Reverse().ToArray());
        // Network ranks its own top list: a quiet process with traffic is kept although CPU, RAM and I/O would drop it.
        var talker = Row(900, 0, 0, 0, 0); talker.SetNetwork(new(1000, 3000, 4000), false, false, false);
        Check(ties.Append(talker).ToArray());
        Require(ProcessHistoryRanking.Pick(ties.Append(talker).ToArray()).Contains(talker), "History ranking dropped the top network process.");
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
        steady.SetNetwork(new(500, 1500, 2000), true, false, false);
        store.Capture(4_000_000_000L, 0, 4, [steady], null, null);
        var range = store.Select(1_000_000_000L, 3_000_000_000L);
        var withNetwork = store.Select(1_000_000_000L, 4_000_000_000L).Summaries.Single(row => row.Last.Key == steady.Key);
        Require(double.IsNaN(range.Summaries.Single(row => row.Last.Key == steady.Key).Average(ProcessHistoryMetric.Network))
            && withNetwork.Average(ProcessHistoryMetric.Network) == 2000 && withNetwork.Peak(ProcessHistoryMetric.Network) == 2000
            && new ProcessHistoryRow(withNetwork, 4).NetworkText.Contains('/') && store.Select(4_000_000_000L, 4_000_000_000L).NetworkRows.Single().NetworkPartial,
            "History network summary counted unmeasured frames as zero or lost the lower-bound mark.");
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
