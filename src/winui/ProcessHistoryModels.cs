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
    IReadOnlyList<ProcessHistorySample> IoRows, string Notice)
{
    internal bool Point => RequestedStartNs == RequestedEndNs;
    internal IReadOnlyList<ProcessHistorySample> Rows(ProcessHistoryMetric metric) => metric switch { ProcessHistoryMetric.Memory => MemoryRows, ProcessHistoryMetric.Io => IoRows, _ => CpuRows };
}

public sealed partial class ProcessHistoryRow
{
    internal ProcessHistorySample Sample { get; }
    internal ProcessHistoryRow(ProcessHistorySample sample)
    {
        Sample = sample; NameText = sample.Name; PidText = sample.Key.Pid.ToString();
        CpuText = Display.Metric(sample.Cpu, " %"); MemoryText = sample.MemoryState == SampleState.Valid ? Display.Number(sample.WorkingBytes / 1048576d, " MiB", 1) : ProcessHistoryText.State(sample.MemoryState);
        IoText = ProcessHistoryRanking.Score(sample, ProcessHistoryMetric.Io) is var io && double.IsFinite(io)
            ? Display.Number(io / 1048576d, " MiB/s", 2) : "I/O nedostupné";
        TimeText = ProcessHistoryText.Time(sample.TimeNs, sample.WallTimeUnixNs);
    }
    public string NameText { get; }
    public string PidText { get; }
    public string CpuText { get; }
    public string MemoryText { get; }
    public string IoText { get; }
    public string TimeText { get; }
}

[WinRT.GeneratedWinRTExposedType]
public sealed partial class ProcessHistoryRows : ObservableCollection<ProcessHistoryRow>
{
    internal void Replace(IReadOnlyList<ProcessHistorySample> rows)
    {
        Items.Clear(); foreach (var row in rows) Items.Add(new(row));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count))); OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
