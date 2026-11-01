using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed record SnapshotProcess(ProcessKey Key, string Name, uint ParentPid, uint Threads, uint Handles,
    ulong WorkingBytes, ulong PrivateBytes, Metric Cpu, Metric ReadIo, Metric WriteIo, bool Stale);
internal sealed record SnapshotEngine(string Id, string Name, Metric Utilization);
internal sealed record SnapshotProcessor(uint Group, uint Index, Metric Utilization);
internal sealed record SnapshotResource(string Id, ResourceKind Kind, string Title, string Description,
    Metric Primary, Metric Secondary, Metric Tertiary,
    Metric Temperature, bool Stale, SnapshotEngine[] Engines);

internal sealed record SnapshotDocument
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public long CapturedMonotonicNs { get; init; }
    public long ProcessTimeNs { get; init; }
    public long ProcessWallTimeUnixNs { get; init; }
    public long ResourceTimeNs { get; init; }
    public BootIdentityInfo Boot { get; init; }
    public MemoryInfo Memory { get; init; }
    public uint LogicalProcessorCount { get; init; }
    public bool Paused { get; init; }
    public bool Stale { get; init; }
    public bool ProcessError { get; init; }
    public bool ResourceError { get; init; }
    public Metric Cpu { get; init; }
    public SnapshotProcess[] Processes { get; init; } = [];
    public SnapshotProcessor[] Processors { get; init; } = [];
    public SnapshotResource[] Resources { get; init; } = [];

    public static SnapshotDocument Capture(MonitorModel model) => new() {
        CapturedMonotonicNs = NativeMonitor.MonotonicNowNs,
        ProcessTimeNs = model.ProcessTime, ProcessWallTimeUnixNs = model.ProcessWallTimeUnixNs, ResourceTimeNs = model.ResourceTime,
        Boot = model.BootIdentity, Memory = model.Memory, LogicalProcessorCount = model.LogicalProcessors, Paused = model.Paused, Stale = model.Stale,
        ProcessError = model.ProcessError.Length > 0, ResourceError = model.ResourceError.Length > 0,
        Cpu = model.AllResources.FirstOrDefault(row => row.Kind == ResourceKind.Cpu)?.Primary ?? new Metric { State = SampleState.Unavailable },
        Processes = model.Processes.OrderBy(row => row.Key.Pid).Select(row => new SnapshotProcess(row.Key, row.Name, row.ParentPid,
            row.Threads, row.Handles, row.WorkingBytes, row.PrivateBytes, row.Cpu, row.ReadIo, row.WriteIo, row.Stale)).ToArray(),
        Processors = model.Processors.OrderBy(pair => pair.Key.Group).ThenBy(pair => pair.Key.Index)
            .Select(pair => new SnapshotProcessor(pair.Key.Group, pair.Key.Index, pair.Value.LastValue)).ToArray(),
        Resources = model.AllResources.Where(row => row.Kind is not ResourceKind.Cpu and not ResourceKind.Memory)
            .Select(row => new SnapshotResource(row.Id, row.Kind, row.Title, row.Description, row.Primary, row.Secondary, row.Tertiary,
                row.Gpu?.Temperature ?? new Metric { State = SampleState.Unavailable }, row.Stale,
                row.Gpu?.OrderedEngines().Select(engine => new SnapshotEngine(engine.Id, engine.Name, engine.Current)).ToArray() ?? [])).ToArray()
    };
}
