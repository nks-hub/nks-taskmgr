using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal enum ProcessHistoryMetric { Cpu, Memory, Io }
internal sealed record ProcessHistorySample(ProcessKey Key, ulong Created100ns, long TimeNs, long WallTimeUnixNs, string Name,
    int OriginalNameLength, Metric Cpu, ulong WorkingBytes, ulong PrivateBytes, SampleState MemoryState, Metric ReadIo, Metric WriteIo,
    bool Late, MeasurementMetadata? CpuContext, MeasurementMetadata? IoContext);
internal sealed record ProcessHistoryGap(long BeforeNs, long AfterNs, ulong? MissingDeliveryRevisions);
internal sealed record ProcessHistorySelection(long RequestedStartNs, long RequestedEndNs, long FirstRetainedNs, long LastRetainedNs,
    long FirstSelectedNs, long LastSelectedNs, long FirstWallTimeUnixNs, long LastWallTimeUnixNs, int FrameCount, long OriginalRows,
    long StoredRows, int MinimumOriginalCount, int MaximumOriginalCount, int GapCount, ulong KnownMissingRevisions, bool DeliverySequenceUnknown,
    int RetainedFrames, int RetainedRows, long AccountedBytes, ulong EvictedFrames, ulong EvictedRows, long PointOffsetNs,
    IReadOnlyList<ProcessHistoryGap> Gaps, IReadOnlyList<ProcessHistorySample> CpuRows, IReadOnlyList<ProcessHistorySample> MemoryRows,
    IReadOnlyList<ProcessHistorySample> IoRows, IReadOnlyList<ProcessHistorySummary> Summaries, string Notice)
{
    internal bool Point => RequestedStartNs == RequestedEndNs;
    internal IReadOnlyList<ProcessHistorySample> Rows(ProcessHistoryMetric metric) => metric switch { ProcessHistoryMetric.Memory => MemoryRows, ProcessHistoryMetric.Io => IoRows, _ => CpuRows };
}

/// <summary>One process lifetime over a selected interval, aggregated from its retained samples only.</summary>
internal sealed class ProcessHistorySummary(ProcessHistorySample first)
{
    private readonly double[] sum = new double[3], peak = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
    private readonly int[] valid = new int[3];
    internal ProcessHistorySample First { get; } = first;
    internal ProcessHistorySample Last { get; private set; } = first;
    internal int Samples { get; private set; }
    internal void Add(ProcessHistorySample sample)
    {
        Last = sample; ++Samples;
        for (int i = 0; i < 3; ++i) {
            double value = ProcessHistoryRanking.Score(sample, (ProcessHistoryMetric)i);
            if (!double.IsFinite(value)) continue;
            sum[i] += value; ++valid[i]; peak[i] = Math.Max(peak[i], value);
        }
    }
    /// <summary>Sum of valid retained values; ranks lifetimes by what they used across the interval.</summary>
    internal double Total(ProcessHistoryMetric metric) => valid[(int)metric] == 0 ? double.NegativeInfinity : sum[(int)metric];
    internal double Average(ProcessHistoryMetric metric) => valid[(int)metric] == 0 ? double.NaN : sum[(int)metric] / valid[(int)metric];
    internal double Peak(ProcessHistoryMetric metric) => valid[(int)metric] == 0 ? double.NaN : peak[(int)metric];
}

public sealed partial class ProcessHistoryRow
{
    internal ProcessHistorySample Sample { get; }
    internal ProcessHistorySummary? Summary { get; }
    internal ProcessHistoryRow(ProcessHistorySample sample)
    {
        Sample = sample; NameText = sample.Name; PidText = sample.Key.Pid.ToString();
        CpuText = Display.Metric(sample.Cpu, " %"); MemoryText = sample.MemoryState == SampleState.Valid ? Display.Number(sample.WorkingBytes / 1048576d, " MiB", 1) : ProcessHistoryText.State(sample.MemoryState);
        IoText = ProcessHistoryRanking.Score(sample, ProcessHistoryMetric.Io) is var io && double.IsFinite(io)
            ? Display.Number(io / 1048576d, " MiB/s", 2) : Loc.T("I/O nedostupné");
        TimeText = ProcessHistoryText.Time(sample.TimeNs, sample.WallTimeUnixNs);
    }
    internal ProcessHistoryRow(ProcessHistorySummary summary, int frames)
    {
        Sample = summary.Last; Summary = summary; NameText = summary.Last.Name; PidText = summary.Last.Key.Pid.ToString();
        string Pair(ProcessHistoryMetric metric, double scale, string unit, int decimals) => double.IsNaN(summary.Peak(metric)) ? "–"
            : Display.Number(summary.Average(metric) / scale, "", decimals) + " / " + Display.Number(summary.Peak(metric) / scale, unit, decimals);
        CpuText = Pair(ProcessHistoryMetric.Cpu, 1, " %", 1); MemoryText = Pair(ProcessHistoryMetric.Memory, 1048576, " MiB", 0);
        IoText = Pair(ProcessHistoryMetric.Io, 1048576, " MiB/s", 2); TimeText = summary.Samples + " / " + frames;
    }
    public string NameText { get; }
    public string PidText { get; }
    public override string ToString() => NameText + ", PID " + PidText + ", " + TimeText;
    public string CpuText { get; }
    public string MemoryText { get; }
    public string IoText { get; }
    public string TimeText { get; }
}

[WinRT.GeneratedWinRTExposedType]
public sealed partial class ProcessHistoryRows : ObservableCollection<ProcessHistoryRow>
{
    internal void Replace(IEnumerable<ProcessHistoryRow> rows)
    {
        Items.Clear(); foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count))); OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
