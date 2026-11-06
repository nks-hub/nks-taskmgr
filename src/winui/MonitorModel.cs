using System.Collections.ObjectModel;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public enum ProcessMode { All, Active, Tree, Applications }
internal readonly record struct NetworkTotals(ulong ReceivedBytes, ulong SentBytes, long StartedAtUnixNs, SampleState State);

public sealed partial class MonitorModel : IDisposable
{
    internal const long StaleAfterNs = 3_000_000_000;
    private readonly NativeMonitor monitor = NativeMonitor.Open();
    private readonly Dictionary<ProcessKey, ProcessRow> processes = [];
    private readonly Dictionary<string, ResourceRow> resources = new(StringComparer.Ordinal);
    private readonly HashSet<ProcessKey> collapsed = [];
    private readonly HashSet<ProcessKey> currentProcessKeys = [];
    private readonly List<ProcessKey> removedProcessKeys = [];
    private readonly HashSet<(uint, uint)> currentProcessorKeys = [];
    private readonly List<(uint, uint)> removedProcessorKeys = [];
    private readonly HashSet<string> currentResourceIds = new(StringComparer.Ordinal);
    private readonly List<string> removedResourceIds = [];
    private bool held, visualUpdates = true, resourceViewVisible = true, processViewVisible = true;
    private bool resourceMembershipDirty = true;
    private string query = string.Empty;
    private int sortColumn;
    private bool descending;
    public ProcessRows VisibleProcesses { get; } = [];
    public ObservableCollection<ResourceRow> Resources { get; } = [];
    internal NetworkFilterOptions NetworkFilters { get; private set; }
    public IReadOnlyCollection<ProcessRow> Processes => processes.Values;
    internal IEnumerable<ResourceRow> AllResources => resources.Values;
    public Dictionary<(uint Group, uint Index), HistorySeries> Processors { get; } = [];
    public HistorySeries PageFile { get; private set; } = new();
    private readonly HardwareFactCache hardwareFacts = new();
    public Dictionary<string, Dictionary<string, string>> Hardware => hardwareFacts.Values;
    internal Dictionary<string, NetworkTotals> NetworkTransfers { get; } = new(StringComparer.Ordinal);
    public MemoryInfo Memory { get; private set; }
    public BootIdentityInfo BootIdentity { get; private set; } = new() { State = SampleState.Unavailable };
    public string CpuModel { get; private set; } = string.Empty;
    public uint LogicalProcessors { get; private set; }
    public long ProcessTime { get; private set; }
    public long ProcessWallTimeUnixNs { get; private set; }
    public long ResourceTime { get; private set; }
    public double CollectionMs { get; private set; }
    public string ProcessError { get; private set; } = string.Empty;
    public string ResourceError { get; private set; } = string.Empty;
    public bool Paused { get; private set; }
    public bool Stale { get; private set; }
    public uint Interval { get; private set; } = 1000;
    public ProcessMode Mode { get; private set; }
    public bool Held => held;
    public int SortColumn => sortColumn;
    public bool SortDescending => descending;
    private bool ProcessVisualUpdates => visualUpdates && processViewVisible;
    internal ApplicationGroupModel Applications { get; } = new();
    internal void ApplyApplications(ProcessDetailsSnapshot snapshot) { Applications.Apply(snapshot); Rebuild(true); }
    internal void SelectApplication(ProcessKey key) { held = false; Applications.Select(key); Rebuild(true); }

    public void SetPaused(bool value) { monitor.SetPaused(value); Paused = value; }
    internal void SetNetworkFilters(NetworkFilterOptions value)
    {
        if (NetworkFilters == value) return;
        NetworkFilters = value;
        resourceMembershipDirty = true;
        if (visualUpdates) {
            SyncResources();
            if (resourceViewVisible) foreach (var resource in Resources) resource.RefreshPreview();
        }
    }
    internal void SetResourceViewVisible(bool value)
    {
        if (resourceViewVisible == value) return;
        resourceViewVisible = value;
        if (!value) return;
        SyncResources();
        foreach (var resource in Resources) resource.RefreshPreview();
    }
    internal void SetProcessViewVisible(bool value)
    {
        if (processViewVisible == value) return;
        processViewVisible = value;
        if (!value) return;
        Rebuild(true);
        foreach (var row in VisibleProcesses) row.NotifyUpdated();
    }
    public void SetInterval(uint value) { monitor.SetInterval(value); Interval = value; }
    public bool Background { get; private set; }
    public void SetBackground(bool value) { if (Background == value) return; monitor.SetBackground(value); Background = value; }
    public void SetQuery(string value) { held = false; query = value; Rebuild(true); }
    public void SetMode(ProcessMode value) { held = false; Mode = value; Rebuild(true); }
    public void SetHeld(bool value) { if (held == value) return; held = value; if (!held) Rebuild(); }
    public void Sort(int column) { held = false; descending = sortColumn == column ? !descending : column > 1; sortColumn = column; Rebuild(true); }
    public void Toggle(ProcessRow row) { held = false; if (!collapsed.Add(row.Key)) collapsed.Remove(row.Key); Rebuild(true); }
    public bool IsCurrent(ProcessRow row) => !row.Ended && processes.TryGetValue(row.Key, out var current) && ReferenceEquals(row, current);
    public IEnumerable<ProcessRow> TopConsumers(bool memory) => memory
        ? processes.Values.Where(row => row.Key.Pid != 0).OrderByDescending(row => row.WorkingBytes).Take(3)
        : processes.Values.Where(row => row.Key.Pid != 0 && row.Cpu.State == SampleState.Valid).OrderByDescending(row => row.Cpu.Value).Take(3);

    public bool Tick(bool updateView = true)
    {
        bool resumed = updateView && !visualUpdates;
        visualUpdates = updateView;
        if (resumed) collectedBindingsDirty = true;
        bool changed = resumed;
        using var frame = monitor.Read();
        if (frame is not null) {
            FrameFlags hardwareScope = 0;
            ReadMeasurements(frame);
            frame.Read(view => {
                hardwareScope = view.Flags;
                if ((view.Flags & FrameFlags.ProcessData) != 0)
                    BootIdentity = frame.ReadBootIdentity() ?? new BootIdentityInfo { State = SampleState.Unavailable };
                ApplyFrame(frame, view);
                ReadSessions(frame, view);
                if ((view.Flags & FrameFlags.ResourceData) != 0) {
                    frame.ReadGpu(data => ApplyGpu(data, false));
                    frame.ReadNetworkTransfers(ApplyNetworkTransfers);
                }
            });
            frame.ReadHardware(data => ApplyHardware(data, hardwareScope));
            changed = true;
        }
        changed |= ReadCollectedHistory();
        long now = NativeMonitor.MonotonicNowNs;
        bool stale = !Paused && ProcessTime > 0 && now - ProcessTime > StaleAfterNs;
        if (stale != Stale) {
            Stale = stale;
            foreach (var row in processes.Values) row.SetStatus(false, stale, ProcessVisualUpdates);
            branchTotalsDirty = true;
            if (RefreshProcessBranchValues() && BranchSort) Rebuild();
            changed = true;
        }
        CaptureProcessHistory();
        foreach (var resource in resources.Values) {
            long measured = resource.Kind is ResourceKind.Cpu or ResourceKind.Memory ? ProcessTime : ResourceTime;
            if (resource.SetStale(!Paused && measured > 0 && now - measured > StaleAfterNs, visualUpdates && resourceViewVisible)) changed = true;
        }
        if (resumed) {
            SyncResources();
            if (ProcessVisualUpdates) {
                Rebuild();
                foreach (var row in VisibleProcesses) row.NotifyUpdated();
            }
        }
        if (changed && visualUpdates) {
            SyncResources();
            if (resourceViewVisible) foreach (var resource in Resources) resource.RefreshPreview();
        }
        if (changed && ProcessVisualUpdates && Mode == ProcessMode.Applications) Applications.RefreshMetrics(held);
        return changed;
    }

    // Frame buffers remain owned by the caller and are copied before this returns.
    internal void Apply(FrameView view) => Apply(view, default, true);

    internal void Apply(FrameView view, ProcessCreationData creation, BootIdentityInfo bootIdentity)
    {
        BootIdentity = bootIdentity;
        Apply(view, creation, true);
    }

    private unsafe void Apply(FrameView view, ProcessCreationData creation, bool recordHistory, ProcessChangesData? changes = null)
    {
        if ((view.Flags & (FrameFlags.ProcessData | FrameFlags.ResourceData)) != 0) collectedBindingsDirty = true;
        if ((view.Flags & FrameFlags.ProcessErrorChanged) != 0) ProcessError = view.ProcessError.Copy();
        if ((view.Flags & FrameFlags.ResourceErrorChanged) != 0) ResourceError = view.ResourceError.Copy();
        if ((view.Flags & FrameFlags.ProcessData) != 0) {
            branchTotalsDirty = true;
            ProcessTime = view.Processes.MeasuredAtNs; ProcessWallTimeUnixNs = view.Processes.WallTimeUnixNs; Memory = view.Processes.Memory;
            CollectionMs = view.Processes.CollectionNs / 1_000_000d;
            LogicalProcessors = view.Processes.LogicalProcessors;
            CpuModel = view.Processes.CpuModel.Copy();
            if (!ApplyProcessChanges(view, creation, changes, out bool orderChanged)) {
                currentProcessKeys.Clear();
                for (uint i = 0; i < view.Processes.ProcessCount; ++i) {
                    var source = view.Processes.Processes[i];
                    var key = new ProcessKey(BootIdentity.Identifier, source.Pid, source.GenerationKind, source.Generation);
                    currentProcessKeys.Add(key); UpdateProcess(source, key, i, creation);
                }
                removedProcessKeys.Clear();
                foreach (var key in processes.Keys) if (!currentProcessKeys.Contains(key)) removedProcessKeys.Add(key);
                foreach (var key in removedProcessKeys) {
                    processes[key].SetStatus(true, false, ProcessVisualUpdates); processes.Remove(key);
                }
            }
            processRevision = changes?.Revision ?? 0;
            Resource("cpu", ResourceKind.Cpu).Update(Loc.T("Procesor"), CpuModel, string.Empty, view.Processes.CpuPercent, default, default, 100, ProcessTime, visualUpdates && resourceViewVisible && recordHistory, recordHistory);
            var memory = new Metric { Value = Memory.PhysicalBytes - Math.Min(Memory.PhysicalBytes, Memory.AvailableBytes), State = Memory.State, Source = MetricSource.PerformanceInfo };
            Resource("memory", ResourceKind.Memory).Update(Loc.T("Paměť"), Loc.T("Fyzická paměť"), string.Empty, memory, default, default, Memory.PhysicalBytes, ProcessTime, visualUpdates && resourceViewVisible && recordHistory, recordHistory);
            if (recordHistory) PageFile.Append(ProcessTime, new Metric { Value = Memory.PageFileUsedBytes, State = Memory.PageFileState, Source = MetricSource.PerformanceInfo });
            currentProcessorKeys.Clear();
            for (uint i = 0; i < view.Processes.ProcessorCount; ++i) {
                var processor = view.Processes.Processors[i]; var key = (processor.Group, processor.Index); currentProcessorKeys.Add(key);
                if (!Processors.TryGetValue(key, out var series)) Processors.Add(key, series = new HistorySeries());
                if (recordHistory) series.Append(ProcessTime, processor.CpuPercent);
            }
            removedProcessorKeys.Clear();
            foreach (var key in Processors.Keys) if (!currentProcessorKeys.Contains(key)) removedProcessorKeys.Add(key);
            foreach (var key in removedProcessorKeys) Processors.Remove(key);
            orderChanged |= ApplyProcessNetwork();
            bool branchChanged = RefreshProcessBranchValues();
            if ((orderChanged || BranchSort && branchChanged) && !held && ProcessVisualUpdates) Rebuild();
        }
        if ((view.Flags & FrameFlags.ResourceData) != 0) {
            ResourceTime = view.Resources.MeasuredAtNs;
            currentResourceIds.Clear(); currentResourceIds.Add("cpu"); currentResourceIds.Add("memory");
            for (uint i = 0; i < view.Resources.ResourceCount; ++i) {
                var source = view.Resources.Resources[i];
                var id = source.Id.Length == 0 ? ReadOnlySpan<char>.Empty : new ReadOnlySpan<char>(source.Id.Data, checked((int)source.Id.Length));
                if (!resources.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(id, out var row)) row = Resource(id.ToString(), source.Kind);
                currentResourceIds.Add(row.Id);
                bool included = NetworkResourceFilter.Includes(row, NetworkFilters);
                row.Update(source, ResourceTime, visualUpdates && resourceViewVisible && recordHistory, recordHistory);
                resourceMembershipDirty |= included != NetworkResourceFilter.Includes(row, NetworkFilters);
            }
            removedResourceIds.Clear();
            foreach (var id in resources.Keys) if (!currentResourceIds.Contains(id)) removedResourceIds.Add(id);
            foreach (var id in removedResourceIds) {
                if (visualUpdates) Resources.Remove(resources[id]);
                resources.Remove(id);
                resourceMembershipDirty = true;
            }
        }
    }

    private ResourceRow Resource(string id, ResourceKind kind)
    {
        if (!resources.TryGetValue(id, out var row)) {
            resources.Add(id, row = new ResourceRow(id, kind));
            resourceMembershipDirty = true;
            if (visualUpdates) {
                if (kind == ResourceKind.Cpu) Resources.Insert(0, row);
                else if (kind == ResourceKind.Memory) Resources.Insert(Math.Min(1, Resources.Count), row);
                else Resources.Add(row);
            }
        }
        return row;
    }

    private unsafe void ApplyGpu(GpuData data, bool recordHistory = true)
    {
        for (uint i = 0; i < data.GpuCount; ++i) {
            var gpu = data.Gpus[i];
            if (resources.TryGetValue(gpu.ResourceId.Copy(), out var row)) row.Gpu?.Apply(gpu, row, ResourceTime, recordHistory);
        }
    }

    private unsafe void ApplyNetworkTransfers(NetworkTransferData data)
    {
        NetworkTransfers.Clear();
        for (uint i = 0; i < data.AdapterCount; ++i) {
            var item = data.Adapters[i];
            NetworkTransfers[item.ResourceId.Copy()] = new(item.ReceivedBytes, item.SentBytes, item.StartedAtUnixNs, item.State);
        }
    }

    // Only physical adapters: VPN and virtual interfaces carry the same bytes a second time.
    internal NetworkTotals? PhysicalNetworkTotals()
    {
        NetworkTotals? sum = null;
        foreach (var row in resources.Values) {
            if (row.Kind != ResourceKind.Network || !row.HardwareFacts.TryGetValue("Druh rozhraní", out var type) || type != "Fyzické") continue;
            if (!NetworkTransfers.TryGetValue(row.Id, out var item) || item.State != SampleState.Valid) return new(0, 0, 0, SampleState.Unavailable);
            sum = sum is { } s ? new(s.ReceivedBytes + item.ReceivedBytes, s.SentBytes + item.SentBytes, Math.Min(s.StartedAtUnixNs, item.StartedAtUnixNs), SampleState.Valid) : item;
        }
        return sum;
    }

    private unsafe void ApplyHardware(HardwareData data, FrameFlags scope)
    {
        hardwareFacts.Apply(data, scope);
        foreach (var row in resources.Values) {
            bool included = NetworkResourceFilter.Includes(row, NetworkFilters);
            row.HardwareFacts = Hardware.TryGetValue(row.Id, out var values) ? values : EmptyHardwareFacts;
            resourceMembershipDirty |= included != NetworkResourceFilter.Includes(row, NetworkFilters);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyHardwareFacts = new Dictionary<string, string>();

    private void SyncResources()
    {
        if (!resourceMembershipDirty) return;
        var rows = resources.Values.Where(row => NetworkResourceFilter.Includes(row, NetworkFilters))
            .OrderBy(row => row.Kind == ResourceKind.Cpu ? 0 : row.Kind == ResourceKind.Memory ? 1 : 2).ToArray();
        var wanted = rows.ToHashSet();
        for (int i = Resources.Count - 1; i >= 0; --i) if (!wanted.Contains(Resources[i])) Resources.RemoveAt(i);
        for (int i = 0; i < rows.Length; ++i) {
            if (i < Resources.Count && ReferenceEquals(Resources[i], rows[i])) continue;
            int found = Resources.IndexOf(rows[i]);
            if (found >= 0) Resources.Move(found, i); else Resources.Insert(i, rows[i]);
        }
        resourceMembershipDirty = false;
    }

    private List<ProcessRow> Ordered()
    {
        IEnumerable<ProcessRow> result = processes.Values.Where(row =>
            (Mode != ProcessMode.Active || (row.Cpu.State == SampleState.Valid && row.Cpu.Value > 0)
                || (row.ReadIo.State == SampleState.Valid && row.ReadIo.Value > 0) || (row.WriteIo.State == SampleState.Valid && row.WriteIo.Value > 0))
            && (Mode != ProcessMode.Applications || Applications.Contains(row))
            && (query.Length == 0 || Mode == ProcessMode.Applications && Applications.Selected?.CaptionMatches(query) == true
                || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || row.PidText.Contains(query, StringComparison.Ordinal)));
        Comparison<ProcessRow> comparison = sortColumn switch {
            1 => (a, b) => a.Key.Pid.CompareTo(b.Key.Pid),
            >= 2 and <= 7 when BranchValuesEnabled => (a, b) => BranchSortValue(a.BranchTotal).CompareTo(BranchSortValue(b.BranchTotal)),
            7 => (a, b) => a.Network.SortValue.CompareTo(b.Network.SortValue),
            2 => (a, b) => a.Cpu.Value.CompareTo(b.Cpu.Value), 3 => (a, b) => a.WorkingBytes.CompareTo(b.WorkingBytes),
            4 => (a, b) => a.PrivateBytes.CompareTo(b.PrivateBytes), 5 => (a, b) => a.ReadIo.Value.CompareTo(b.ReadIo.Value),
            6 => (a, b) => a.WriteIo.Value.CompareTo(b.WriteIo.Value),
            _ => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name)
        };
        var rows = result.ToList();
        rows.Sort((a, b) => {
            int order = comparison(a, b); if (order != 0) return descending ? -order : order;
            order = a.Key.Pid.CompareTo(b.Key.Pid); return order != 0 ? order : a.Key.Generation.CompareTo(b.Key.Generation);
        });
        return rows;
    }

    private void Rebuild(bool explicitChange = false)
    {
        if (!explicitChange && !ProcessVisualUpdates) return;
        RefreshProcessBranchValues(explicitChange);
        if (held) return;
        if (Mode == ProcessMode.Applications) Applications.Refresh(processes.Values, query);
        var rows = Ordered();
        if (Mode is ProcessMode.Tree or ProcessMode.Applications && query.Length == 0) rows = ProcessTree.Order(rows, collapsed);
        else foreach (var row in rows) row.SetTree(0, false, false);
        VisibleProcesses.Apply(rows);
        collapsed.RemoveWhere(key => !processes.ContainsKey(key));
    }

    public void Dispose() => monitor.Dispose();

    internal void ReplayForBenchmark(IEnumerable<ProcessRow> rows)
    {
        processes.Clear();
        foreach (var row in rows) processes.Add(row.Key, row);
        branchTotalsDirty = true;
        Rebuild(true);
    }
}
