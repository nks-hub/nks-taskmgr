using System.Collections.ObjectModel;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public enum ProcessMode { All, Active, Tree }

public sealed class MonitorModel : IDisposable
{
    private readonly NativeMonitor monitor = NativeMonitor.Open();
    private readonly Dictionary<ProcessKey, ProcessRow> processes = [];
    private readonly Dictionary<string, ResourceRow> resources = [];
    private readonly HashSet<ProcessKey> collapsed = [];
    private bool held, visualUpdates = true;
    private string query = string.Empty;
    private int sortColumn;
    private bool descending;
    public ProcessRows VisibleProcesses { get; } = [];
    public ObservableCollection<ResourceRow> Resources { get; } = [];
    public IReadOnlyCollection<ProcessRow> Processes => processes.Values;
    public Dictionary<(uint Group, uint Index), HistorySeries> Processors { get; } = [];
    public HistorySeries PageFile { get; } = new();
    public Dictionary<string, Dictionary<string, string>> Hardware { get; } = [];
    public MemoryInfo Memory { get; private set; }
    public BootIdentityInfo BootIdentity { get; private set; } = new() { State = SampleState.Unavailable };
    public string CpuModel { get; private set; } = string.Empty;
    public uint LogicalProcessors { get; private set; }
    public long ProcessTime { get; private set; }
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

    public void SetPaused(bool value) { monitor.SetPaused(value); Paused = value; }
    public void SetInterval(uint value) { monitor.SetInterval(value); Interval = value; }
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
        bool changed = resumed;
        using var frame = monitor.Read();
        if (frame is not null) {
            frame.Read(view => {
                if ((view.Flags & FrameFlags.ProcessData) != 0)
                    BootIdentity = frame.ReadBootIdentity() ?? new BootIdentityInfo { State = SampleState.Unavailable };
                Apply(view); if ((view.Flags & FrameFlags.ResourceData) != 0) frame.ReadGpu(ApplyGpu);
            });
            frame.ReadHardware(ApplyHardware);
            changed = true;
        }
        bool stale = !Paused && ProcessTime > 0 && NativeMonitor.MonotonicNowNs - ProcessTime > 3_000_000_000;
        if (stale != Stale) {
            Stale = stale;
            foreach (var row in processes.Values) row.SetStatus(false, stale, visualUpdates);
            changed = true;
        }
        foreach (var resource in resources.Values) {
            long measured = resource.Kind is ResourceKind.Cpu or ResourceKind.Memory ? ProcessTime : ResourceTime;
            if (resource.SetStale(!Paused && measured > 0 && NativeMonitor.MonotonicNowNs - measured > 3_000_000_000, visualUpdates)) changed = true;
        }
        if (resumed) {
            SyncResources();
            Rebuild();
            foreach (var row in VisibleProcesses) row.NotifyUpdated();
            foreach (var resource in resources.Values) resource.RefreshPreview();
        }
        return changed;
    }

    private unsafe void Apply(FrameView view)
    {
        if ((view.Flags & FrameFlags.ProcessErrorChanged) != 0) ProcessError = view.ProcessError.Copy();
        if ((view.Flags & FrameFlags.ResourceErrorChanged) != 0) ResourceError = view.ResourceError.Copy();
        if ((view.Flags & FrameFlags.ProcessData) != 0) {
            ProcessTime = view.Processes.MeasuredAtNs; Memory = view.Processes.Memory;
            CollectionMs = view.Processes.CollectionNs / 1_000_000d;
            LogicalProcessors = view.Processes.LogicalProcessors;
            CpuModel = view.Processes.CpuModel.Copy();
            var seen = new HashSet<ProcessKey>();
            for (uint i = 0; i < view.Processes.ProcessCount; ++i) {
                var source = view.Processes.Processes[i];
                var key = new ProcessKey(BootIdentity.Identifier, source.Pid, source.GenerationKind, source.Generation);
                seen.Add(key);
                if (!processes.TryGetValue(key, out var row)) processes.Add(key, row = new ProcessRow(key));
                row.Update(source, visualUpdates);
            }
            foreach (var key in processes.Keys.Where(key => !seen.Contains(key)).ToArray()) {
                processes[key].SetStatus(true, false, visualUpdates); processes.Remove(key);
            }
            Resource("cpu", ResourceKind.Cpu).Update("Procesor", CpuModel, string.Empty, view.Processes.CpuPercent, default, default, 100, ProcessTime, visualUpdates);
            var memory = new Metric { Value = Memory.PhysicalBytes - Math.Min(Memory.PhysicalBytes, Memory.AvailableBytes), State = Memory.State, Source = MetricSource.PerformanceInfo };
            Resource("memory", ResourceKind.Memory).Update("Paměť", "Fyzická paměť", string.Empty, memory, default, default, Memory.PhysicalBytes, ProcessTime, visualUpdates);
            PageFile.Append(ProcessTime, new Metric { Value = Memory.PageFileUsedBytes, State = Memory.PageFileState, Source = MetricSource.PerformanceInfo });
            var processorKeys = new HashSet<(uint, uint)>();
            for (uint i = 0; i < view.Processes.ProcessorCount; ++i) {
                var processor = view.Processes.Processors[i]; var key = (processor.Group, processor.Index); processorKeys.Add(key);
                if (!Processors.TryGetValue(key, out var series)) Processors.Add(key, series = new HistorySeries());
                series.Append(ProcessTime, processor.CpuPercent);
            }
            foreach (var key in Processors.Keys.Where(key => !processorKeys.Contains(key)).ToArray()) Processors.Remove(key);
            if (!held && visualUpdates) Rebuild();
        }
        if ((view.Flags & FrameFlags.ResourceData) != 0) {
            ResourceTime = view.Resources.MeasuredAtNs;
            var seen = new HashSet<string> { "cpu", "memory" };
            for (uint i = 0; i < view.Resources.ResourceCount; ++i) {
                var source = view.Resources.Resources[i]; var id = source.Id.Copy(); seen.Add(id);
                Resource(id, source.Kind).Update(source.Title.Copy(), source.Description.Copy(), source.Status.Copy(),
                    source.Primary, source.Secondary, source.Tertiary, source.Capacity, ResourceTime, visualUpdates);
            }
            foreach (var id in resources.Keys.Where(id => !seen.Contains(id)).ToArray()) {
                if (visualUpdates) Resources.Remove(resources[id]);
                resources.Remove(id);
            }
        }
    }

    private ResourceRow Resource(string id, ResourceKind kind)
    {
        if (!resources.TryGetValue(id, out var row)) {
            resources.Add(id, row = new ResourceRow(id, kind));
            if (visualUpdates) {
                if (kind == ResourceKind.Cpu) Resources.Insert(0, row);
                else if (kind == ResourceKind.Memory) Resources.Insert(Math.Min(1, Resources.Count), row);
                else Resources.Add(row);
            }
        }
        return row;
    }

    private unsafe void ApplyGpu(GpuData data)
    {
        for (uint i = 0; i < data.GpuCount; ++i) {
            var gpu = data.Gpus[i];
            if (resources.TryGetValue(gpu.ResourceId.Copy(), out var row)) row.Gpu?.Apply(gpu, row, ResourceTime);
        }
    }

    private unsafe void ApplyHardware(HardwareData data)
    {
        var grouped = new Dictionary<string, Dictionary<string, string>>();
        for (uint i = 0; i < data.FactCount; ++i) {
            var fact = data.Facts[i]; string id = fact.ResourceId.Copy();
            if (!grouped.TryGetValue(id, out var values)) grouped.Add(id, values = []);
            values[fact.Name.Copy()] = fact.Value.Copy();
        }
        foreach (var (id, values) in grouped) {
            Hardware[id] = values;
            if (resources.TryGetValue(id, out var row)) row.HardwareFacts = values;
        }
    }

    private void SyncResources()
    {
        var wanted = resources.Values.ToHashSet();
        for (int i = Resources.Count - 1; i >= 0; --i) if (!wanted.Contains(Resources[i])) Resources.RemoveAt(i);
        var rows = resources.Values.OrderBy(row => row.Kind == ResourceKind.Cpu ? 0 : row.Kind == ResourceKind.Memory ? 1 : 2).ToArray();
        for (int i = 0; i < rows.Length; ++i) {
            if (i < Resources.Count && ReferenceEquals(Resources[i], rows[i])) continue;
            int found = Resources.IndexOf(rows[i]);
            if (found >= 0) Resources.Move(found, i); else Resources.Insert(i, rows[i]);
        }
    }

    private List<ProcessRow> Ordered()
    {
        IEnumerable<ProcessRow> result = processes.Values.Where(row =>
            (Mode != ProcessMode.Active || (row.Cpu.State == SampleState.Valid && row.Cpu.Value > 0)
                || (row.ReadIo.State == SampleState.Valid && row.ReadIo.Value > 0) || (row.WriteIo.State == SampleState.Valid && row.WriteIo.Value > 0))
            && (query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || row.PidText.Contains(query, StringComparison.Ordinal)));
        Comparison<ProcessRow> comparison = sortColumn switch {
            1 => (a, b) => a.Key.Pid.CompareTo(b.Key.Pid),
            2 => (a, b) => a.Cpu.Value.CompareTo(b.Cpu.Value),
            3 => (a, b) => a.WorkingBytes.CompareTo(b.WorkingBytes),
            4 => (a, b) => a.PrivateBytes.CompareTo(b.PrivateBytes),
            5 => (a, b) => a.ReadIo.Value.CompareTo(b.ReadIo.Value),
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
        if (held || (!explicitChange && !visualUpdates)) return;
        var rows = Ordered();
        foreach (var row in rows) row.SetTree(0, false, false);
        if (Mode == ProcessMode.Tree && query.Length == 0) rows = ProcessTree.Order(rows, collapsed);
        VisibleProcesses.Apply(rows);
        collapsed.RemoveWhere(key => !processes.ContainsKey(key));
    }

    public void Dispose() => monitor.Dispose();

    internal void ReplayForBenchmark(IEnumerable<ProcessRow> rows)
    {
        processes.Clear();
        foreach (var row in rows) processes.Add(row.Key, row);
        Rebuild(true);
    }
}
