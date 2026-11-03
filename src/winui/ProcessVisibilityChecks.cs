using System.Collections.Specialized;
using System.ComponentModel;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    internal static void ProcessViewVisibility()
    {
        HiddenProcessData(); HiddenProcessBranches(); ProcessVisibilityCost();
        App.Trace("Process view visibility passed: raw lifetimes/history stay fresh; hidden notifications/order are deferred; explicit operations and visible/held/collapsed return remain current");
    }

    private sealed class VisibilityWatch : IDisposable
    {
        private readonly MonitorModel model;
        private readonly HashSet<ProcessRow> rows = [];
        private readonly ResourceRow[] resources;
        private readonly PropertyChangedEventHandler processChanged, resourceChanged;
        private readonly NotifyCollectionChangedEventHandler collectionChanged;
        internal int Properties, Collections, ResourceProperties;
        internal VisibilityWatch(MonitorModel model)
        {
            this.model = model; resources = model.Resources.ToArray();
            processChanged = (_, _) => ++Properties; resourceChanged = (_, _) => ++ResourceProperties;
            collectionChanged = (_, _) => ++Collections; ObserveRows();
            foreach (var row in resources) row.PropertyChanged += resourceChanged;
            model.VisibleProcesses.CollectionChanged += collectionChanged;
        }
        internal void ObserveRows() { foreach (var row in model.Processes) if (rows.Add(row)) row.PropertyChanged += processChanged; }
        internal void Reset() => Properties = Collections = ResourceProperties = 0;
        public void Dispose()
        {
            foreach (var row in rows) row.PropertyChanged -= processChanged;
            foreach (var row in resources) row.PropertyChanged -= resourceChanged;
            model.VisibleProcesses.CollectionChanged -= collectionChanged;
        }
    }

    private static ProcessCreationInfo[] VisibilityCreation(ProcessInfo[] rows) => rows.Select(row => new ProcessCreationInfo {
        Pid = row.Pid, Kind = row.GenerationKind, Generation = row.Generation,
        Created100ns = row.GenerationKind == GenerationKind.CreationTime ? row.Generation : row.Pid * 100UL, State = SampleState.Valid
    }).ToArray();

    private static unsafe void ApplyVisibilityFrame(MonitorModel model, ProcessInfo[] rows, ProcessCreationInfo[] creation, long start, int sample)
    {
        const ulong gib = 1073741824, mib = 1048576;
        fixed (ProcessInfo* data = rows)
        fixed (ProcessCreationInfo* times = creation)
        fixed (char* id = "visibility-network", name = "Fixture network", status = "Připojeno", cpu = "Fixture CPU") {
            var network = new ResourceInfo { Kind = ResourceKind.Network, Id = new String16 { Data = id, Length = 18 },
                Title = new String16 { Data = name, Length = 15 }, Status = new String16 { Data = status, Length = 9 },
                Primary = new Metric { Value = (sample + 1) * 1000, State = SampleState.Valid },
                Secondary = new Metric { Value = (sample + 1) * 2000, State = SampleState.Valid }, Capacity = 100000000 };
            var processor = new ProcessorInfo { CpuPercent = new Metric { Value = 10 + sample, State = SampleState.Valid } };
            long time = start + sample * 1000000L;
            model.Apply(new FrameView { Flags = FrameFlags.ProcessData | FrameFlags.ResourceData,
                Processes = new ProcessData { MeasuredAtNs = time, LogicalProcessors = 1, Processes = data, ProcessCount = (uint)rows.Length,
                    CpuModel = new String16 { Data = cpu, Length = 11 }, CpuPercent = processor.CpuPercent, Processors = &processor, ProcessorCount = 1,
                    Memory = new MemoryInfo { PhysicalBytes = 16 * gib, AvailableBytes = 12 * gib - (ulong)sample * mib, State = SampleState.Valid,
                        PageFileUsedBytes = 128 * mib + (ulong)sample * mib, PageFileState = SampleState.Valid } },
                Resources = new ResourceData { MeasuredAtNs = time, Resources = &network, ResourceCount = 1 }
            }, new ProcessCreationData { Processes = times, ProcessCount = (uint)creation.Length },
                new BootIdentityInfo { Low = 1, High = 2, State = SampleState.Valid });
        }
        // Tick captures process history separately after applying a frame.
        model.CaptureProcessHistory();
    }

    private static void HiddenProcessData()
    {
        using var model = new MonitorModel(); long start = NativeMonitor.MonotonicNowNs;
        var initial = new[] { BranchProcess(10, 1000, 5), BranchProcess(20, 7, 7, 10), BranchProcess(30, 3000, 3) };
        initial[1].GenerationKind = GenerationKind.SequenceNumber;
        var creation = VisibilityCreation(initial); ApplyVisibilityFrame(model, initial, creation, start, 0); model.Sort(2);
        var parent = model.Processes.Single(row => row.Key.Pid == 10);
        var child = model.Processes.Single(row => row.Key.Pid == 20);
        var old = model.Processes.Single(row => row.Key.Pid == 30);
        using var watch = new VisibilityWatch(model);
        initial[0].CpuPercent.Value = 6; ApplyVisibilityFrame(model, initial, creation, start, 1);
        Require(watch.Properties > 0, "Process bindings were disabled for existing visible consumers by default.");
        watch.Reset(); var visible = model.VisibleProcesses.ToArray(); model.SetProcessViewVisible(false);
        var next = new[] { BranchProcess(10, 1000, 11), initial[1], BranchProcess(30, 4000, 17), BranchProcess(40, 5000, 21) };
        next[1].CpuPercent.Value = 13; next[1].WorkingSetBytes = 8 * 1048576;
        creation = VisibilityCreation(next); creation[1].Created100ns = 2500;
        ApplyVisibilityFrame(model, next, creation, start, 2); watch.ObserveRows();
        for (int i = 0; i < next.Length; ++i) ++next[i].CpuPercent.Value;
        ApplyVisibilityFrame(model, next, creation, start, 3);
        var added = model.Processes.Single(row => row.Key.Pid == 40);
        var replacement = model.Processes.Single(row => row.Key.Pid == 30);
        Require(watch.Properties == 0 && watch.Collections == 0 && visible.SequenceEqual(model.VisibleProcesses, ReferenceEqualityComparer.Instance),
            "A hidden process frame notified bindings or changed the displayed membership/order.");
        Require(model.Processes.Count == 4 && model.IsCurrent(parent) && model.IsCurrent(child) && model.IsCurrent(added)
            && !model.IsCurrent(old) && old.Ended && !ReferenceEquals(old, replacement) && replacement.Key.Generation == 4000
            && parent.Cpu.Value == 12 && child.Cpu.Value == 14 && child.WorkingBytes == 8 * 1048576
            && child.Key.Generation == 7 && child.Created100ns == 2500, "Hidden process data, PID reuse or independent creation metadata stopped updating.");
        var cpu = model.Resources.Single(row => row.Kind == ResourceKind.Cpu);
        var memory = model.Resources.Single(row => row.Kind == ResourceKind.Memory);
        var network = model.Resources.Single(row => row.Id == "visibility-network");
        var history = model.SelectProcessHistory(start, start + 3000000);
        Require(model.ProcessTime == start + 3000000 && model.ResourceTime == model.ProcessTime && watch.ResourceProperties > 0
            && cpu.Primary.Value == 13 && cpu.History.Count == 4 && memory.Primary.Value == 4UL * 1073741824 + 3UL * 1048576
            && network.Primary.Value == 4000 && network.Secondary.Value == 8000 && network.History.Count == 4 && network.Upload.Count == 4
            && model.PageFile.Count == 4 && model.PageFile.LastValue.Value == 131UL * 1048576 && history.FrameCount == 4
            && history.CpuRows.Any(row => row.Key == added.Key && row.Cpu.Value == 22), "Hiding only processes stopped resource metrics, intermediate history or capture of a new lifetime.");
        model.SetQuery("40"); Require(model.VisibleProcesses.Count == 1 && ReferenceEquals(model.VisibleProcesses[0], added), "Hidden explicit search did not resynchronize.");
        model.SetMode(ProcessMode.Active); model.Sort(1); model.Sort(2); model.SetQuery("");
        Require(model.Mode == ProcessMode.Active && model.VisibleProcesses.Count == 4, "Hidden explicit mode/sort did not resynchronize.");
        watch.Reset(); visible = model.VisibleProcesses.ToArray(); next[0].CpuPercent.Value = 99;
        ApplyVisibilityFrame(model, next, creation, start, 4);
        Require(watch.Properties == 0 && watch.Collections == 0 && visible.SequenceEqual(model.VisibleProcesses, ReferenceEqualityComparer.Instance),
            "An explicit hidden operation permanently re-enabled automatic process binding updates.");
        long before = model.ProcessTime; model.SetProcessViewVisible(true);
        Require(model.ProcessTime == before && watch.Properties >= model.VisibleProcesses.Count && ReferenceEquals(model.VisibleProcesses[0], parent),
            "Showing processes without another frame failed to publish fresh sorted rows immediately.");
    }

    private static void HiddenProcessBranches()
    {
        using var model = new MonitorModel();
        var rows = new[] { BranchProcess(10, 1000, 5), BranchProcess(20, 2000, 7, 10), BranchProcess(30, 3000, 3, 20), BranchProcess(40, 4000, 20) };
        ApplyProcesses(model, rows); model.SetMode(ProcessMode.Tree); model.SetTableValueMode(ProcessTableValueMode.ProcessWithDescendants); model.Sort(2);
        var parent = model.Processes.Single(row => row.Key.Pid == 10); model.Toggle(parent); model.SetHeld(true);
        var visible = model.VisibleProcesses.ToArray(); var previous = parent.BranchTotal;
        using var watch = new VisibilityWatch(model); model.SetProcessViewVisible(false);
        rows[1].CpuPercent.Value = 40; ApplyProcesses(model, rows);
        Require(watch.Properties == 0 && watch.Collections == 0 && parent.BranchTotal == previous && parent.Collapsed,
            "A hidden child update computed/notified branch totals or changed collapse state.");
        model.SetProcessViewVisible(true);
        Require(model.Held && parent.Collapsed && visible.SequenceEqual(model.VisibleProcesses, ReferenceEqualityComparer.Instance)
            && parent.BranchTotal.Members == 3 && parent.BranchTotal.Cpu.Value == 48 && watch.Properties >= visible.Length,
            "Held/collapsed return lost row identities, current branch totals or required binding refresh.");
        model.SetHeld(false);
        Require(ReferenceEquals(model.VisibleProcesses[0], parent) && parent.Collapsed, "Releasing held order did not sort current full branch sums.");
        watch.Reset(); model.SetProcessViewVisible(false); rows[1].CpuPercent.Value = 1; ApplyProcesses(model, rows);
        Require(watch.Properties == 0 && watch.Collections == 0 && parent.BranchTotal.Cpu.Value == 48, "Hidden automatic branch sorting was not deferred.");
        model.SetProcessViewVisible(true);
        Require(model.VisibleProcesses[0].Key.Pid == 40 && parent.BranchTotal.Cpu.Value == 9 && parent.Collapsed,
            "Visible return did not rebuild dirty branch order without another frame.");
    }

    private static void ProcessVisibilityCost()
    {
        const int count = 500, rounds = 8;
        var rows = Enumerable.Range(0, count).Select(i => BranchProcess((uint)(1000 + i), (ulong)(10000 + i), i + 1,
            i % 10 == 0 ? 0 : (uint)(1000 + i / 10 * 10))).ToArray();
        var creation = VisibilityCreation(rows); long start = NativeMonitor.MonotonicNowNs;
        using var visible = new MonitorModel(); using var hidden = new MonitorModel();
        foreach (var model in new[] { visible, hidden }) {
            model.SetResourceViewVisible(false); ApplyVisibilityFrame(model, rows, creation, start, 0);
            model.SetMode(ProcessMode.Tree); model.SetTableValueMode(ProcessTableValueMode.ProcessWithDescendants); model.Sort(2);
        }
        hidden.SetProcessViewVisible(false);
        for (int round = 1; round <= 2; ++round) {
            UpdateVisibilityRows(rows, round); ApplyVisibilityFrame(visible, rows, creation, start, round); ApplyVisibilityFrame(hidden, rows, creation, start, round);
        }
        using var visibleWatch = new VisibilityWatch(visible); using var hiddenWatch = new VisibilityWatch(hidden);
        var frozen = hidden.VisibleProcesses.ToArray(); long visibleBytes = 0, hiddenBytes = 0;
        for (int round = 3; round < 3 + rounds; ++round) {
            UpdateVisibilityRows(rows, round); long before = GC.GetAllocatedBytesForCurrentThread();
            ApplyVisibilityFrame(visible, rows, creation, start, round); visibleBytes += GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread(); ApplyVisibilityFrame(hidden, rows, creation, start, round); hiddenBytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        int hiddenProperties = hiddenWatch.Properties, hiddenCollections = hiddenWatch.Collections;
        Require(visibleWatch.Properties > 0 && visibleWatch.Collections > 0 && hiddenProperties == 0 && hiddenCollections == 0
            && frozen.SequenceEqual(hidden.VisibleProcesses, ReferenceEqualityComparer.Instance), "The same500-row workload did not causally suppress hidden process notifications/order changes.");
        Require(hiddenBytes < visibleBytes, "The hidden500-row workload did not reduce allocation for the same current-model input.");
        var expected = visible.Processes.ToDictionary(row => row.Key);
        Require(hidden.Processes.Count == count && hidden.Processes.All(row => expected.TryGetValue(row.Key, out var other)
            && row.Cpu.Equals(other.Cpu) && row.WorkingBytes == other.WorkingBytes && row.Created100ns == other.Created100ns && hidden.IsCurrent(row))
            && hidden.ProcessTime == visible.ProcessTime && hidden.PageFile.Count == visible.PageFile.Count,
            "The hidden500-row workload saved UI work by dropping raw data/history.");
        hidden.SetProcessViewVisible(true);
        Require(hidden.VisibleProcesses.Select(row => row.Key).SequenceEqual(visible.VisibleProcesses.Select(row => row.Key))
            && hidden.Processes.All(row => row.BranchTotal == expected[row.Key].BranchTotal), "The hidden500-row model did not recover the same current branch values/order.");
        App.Trace(FormattableString.Invariant($"Process visibility cost: scope=synthetic-current-model;rows={count};frames={rounds};visiblePropertyChanges={visibleWatch.Properties};visibleCollectionChanges={visibleWatch.Collections};hiddenPropertyChanges={hiddenProperties};hiddenCollectionChanges={hiddenCollections};visibleBytesPerFrame={(double)visibleBytes / rounds:F0};hiddenBytesPerFrame={(double)hiddenBytes / rounds:F0};wholeAppCpuMeasured=False"));
    }

    private static void UpdateVisibilityRows(ProcessInfo[] rows, int sample)
    {
        for (int i = 0; i < rows.Length; ++i) {
            rows[i].CpuPercent.Value = sample % 2 == 0 ? i + 1 : rows.Length - i;
            rows[i].WorkingSetBytes = (ulong)(sample + i + 1) * 1048576;
        }
    }
}
