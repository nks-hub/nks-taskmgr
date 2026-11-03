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
        App.Trace("History ranking passed: small snapshots, stable ties, input order, idle PID, stale rows, invalid metrics, large memory and duplicate keys");
    }
}
