using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static void ProcessRefresh()
    {
        UnchangedProcessTree();
        SequenceProcessTree();
        CollectedHistoryTopology();
        App.Trace("Process tree refresh passed: unchanged expanded/collapsed trees, search, flat mode and independent raw creation times");
    }

    internal static ProcessInfo Process(uint pid, ulong generation, double cpu, uint parent = 0) => new() {
        Pid = pid, ParentPid = parent, Generation = generation, GenerationKind = GenerationKind.CreationTime,
        CpuPercent = new Metric { Value = cpu, State = SampleState.Valid, Source = MetricSource.NativeProcess }
    };

    internal static void ApplyProcesses(MonitorModel model, params ProcessInfo[] rows)
    {
        var creation = rows.Select(row => new ProcessCreationInfo {
            Pid = row.Pid, Kind = row.GenerationKind, Generation = row.Generation,
            Created100ns = row.GenerationKind == GenerationKind.CreationTime ? row.Generation : 0,
            State = row.GenerationKind == GenerationKind.CreationTime && row.Generation != 0 ? SampleState.Valid : SampleState.Unavailable
        }).ToArray();
        ApplyProcessFixture(model, rows, creation);
    }

    private static unsafe void ApplyProcessFixture(MonitorModel model, ProcessInfo[] rows, ProcessCreationInfo[] creation)
    {
        fixed (ProcessInfo* data = rows)
        fixed (ProcessCreationInfo* times = creation) model.Apply(new FrameView {
            Flags = FrameFlags.ProcessData,
            Processes = new ProcessData { MeasuredAtNs = NativeMonitor.MonotonicNowNs,
                Processes = data, ProcessCount = (uint)rows.Length, LogicalProcessors = 1 }
        }, new ProcessCreationData { Processes = times, ProcessCount = (uint)creation.Length },
            new BootIdentityInfo { Low = 1, High = 2, State = SampleState.Valid });
    }

    private static void UnchangedProcessTree()
    {
        using var model = new MonitorModel();
        var parent = Process(10, 100, 50);
        var child = Process(20, 200, 25, parent.Pid);
        ApplyProcesses(model, parent, child); model.SetMode(ProcessMode.Tree);
        var rows = model.VisibleProcesses.ToArray();
        Require(rows.Length == 2 && rows[0].HasChildren && rows[1].Depth == 1, "Tree fixture did not create a parent and child.");
        var changed = new List<string?>();
        PropertyChangedEventHandler observe = (_, args) => {
            if (args.PropertyName is nameof(ProcessRow.Indent) or nameof(ProcessRow.DisplayName)) changed.Add(args.PropertyName);
        };
        foreach (var row in rows) row.PropertyChanged += observe;
        ApplyProcesses(model, parent, child);
        Require(changed.Count == 0, $"An unchanged expanded process tree raised {changed.Count} Indent/DisplayName changes.");
        model.Toggle(rows[0]); changed.Clear();
        ApplyProcesses(model, parent, child);
        Require(changed.Count == 0 && rows[0].Collapsed && model.VisibleProcesses.Count == 1,
            "An unchanged collapsed process tree invalidated its bindings or expanded its branch.");
        model.SetQuery("20");
        Require(model.VisibleProcesses.Count == 1 && rows[1].Depth == 0 && !rows[1].HasChildren && !rows[1].Collapsed,
            "Tree search retained tree indentation or branch state.");
        model.SetQuery(""); model.SetMode(ProcessMode.All);
        Require(model.VisibleProcesses.Count == 2 && rows.All(row => row.Depth == 0 && !row.HasChildren && !row.Collapsed),
            "Flat process mode retained tree indentation or branch state.");
        foreach (var row in rows) row.PropertyChanged -= observe;
    }

    private static void SequenceProcessTree()
    {
        using var model = new MonitorModel();
        var parent = Process(10, 10000, 50);
        var child = Process(20, 7, 25, parent.Pid); child.GenerationKind = GenerationKind.SequenceNumber;
        var creation = new[] {
            new ProcessCreationInfo { Pid = parent.Pid, Kind = parent.GenerationKind, Generation = parent.Generation, Created100ns = 10000, State = SampleState.Valid },
            new ProcessCreationInfo { Pid = child.Pid, Kind = child.GenerationKind, Generation = child.Generation, Created100ns = 20000, State = SampleState.Valid }
        };
        ApplyProcessFixture(model, [parent, child], creation); model.SetMode(ProcessMode.Tree);
        var rows = model.VisibleProcesses.ToArray();
        Require(rows.Length == 2 && rows[0].HasChildren && rows[1].Depth == 1
            && rows[1].Key.Kind == GenerationKind.SequenceNumber && rows[1].Key.Generation == 7 && rows[1].Created100ns == 20000,
            "A sequence identity was used as a timestamp or prevented a verified mixed-kind parent relation.");
        creation[1].State = SampleState.Unavailable; creation[1].Created100ns = 0;
        ApplyProcessFixture(model, [parent, child], creation);
        Require(model.VisibleProcesses.All(row => !row.HasChildren && row.Depth == 0)
            && rows[1].Created100ns == 0 && rows[1].Key.Generation == 7,
            "A missing raw creation time was replaced with a sequence number for parent verification.");
        creation[1].State = SampleState.Valid; creation[1].Created100ns = 20000; creation[1].Generation = 8;
        ApplyProcessFixture(model, [parent, child], creation);
        Require(rows[1].Created100ns == 0 && model.VisibleProcesses.All(row => !row.HasChildren && row.Depth == 0),
            "Creation metadata from another sequence identity was applied to a recycled PID.");
        creation[1].Generation = child.Generation; creation[1].Created100ns = 5000;
        ApplyProcessFixture(model, [parent, child], creation);
        Require(rows[1].Created100ns == 5000 && model.VisibleProcesses.All(row => !row.HasChildren && row.Depth == 0),
            "A parent created after its child was accepted despite explicit raw creation times.");
    }

    private static unsafe void CollectedHistoryTopology()
    {
        using var model = new MonitorModel();
        const long time = 1_000_000_000;
        fixed (char* cpu = "cpu", disk = "fixture-disk", gpu = "fixture-gpu", engine = "engine-a") {
            var definitions = stackalloc HistoryDefinitionInfo[4];
            definitions[0] = new() { Id = 1, Channel = HistoryChannel.Processor, Group = 2, Index = 3,
                ResourceId = new String16 { Data = cpu, Length = 3 } };
            definitions[1] = new() { Id = 2, Channel = HistoryChannel.Primary,
                ResourceId = new String16 { Data = disk, Length = 12 } };
            definitions[2] = new() { Id = 3, Channel = HistoryChannel.Engine,
                ResourceId = new String16 { Data = gpu, Length = 11 }, DetailId = new String16 { Data = engine, Length = 8 } };
            var points = stackalloc CollectedHistoryPoint[4];
            for (uint i = 0; i < 3; ++i) points[i] = new() { Sequence = i + 1, TimeNs = time, Series = i + 1,
                Metric = new Metric { Value = (i + 1) * 10, State = SampleState.Valid, Source = MetricSource.PerformanceCounter } };
            Require(model.ApplyCollectedHistory(new HistoryView { DefinitionCount = 3, Definitions = definitions, PointCount = 3, Points = points }),
                "Collected definitions and points did not enter the production history path.");
            Require(!model.ApplyCollectedHistory(default), "An empty history batch claimed a sample update.");

            var resources = stackalloc ResourceInfo[2];
            resources[0] = new() { Kind = ResourceKind.Disk, Id = definitions[1].ResourceId };
            resources[1] = new() { Kind = ResourceKind.Gpu, Id = definitions[2].ResourceId };
            var processor = new ProcessorInfo { Group = 2, Index = 3 };
            void ApplyTargets(bool present) {
                model.Apply(new FrameView { Flags = FrameFlags.ResourceData,
                    Resources = new ResourceData { MeasuredAtNs = time, Resources = resources, ResourceCount = present ? 2u : 0 } });
                var value = processor;
                model.Apply(new FrameView { Flags = FrameFlags.ProcessData,
                    Processes = new ProcessData { MeasuredAtNs = time, LogicalProcessors = present ? 1u : 0,
                        Processors = &value, ProcessorCount = present ? 1u : 0 } });
            }
            ApplyTargets(true);
            model.ApplyCollectedHistory(default);
            var diskRow = model.Resources.Single(row => row.Id == "fixture-disk");
            var gpuRow = model.Resources.Single(row => row.Id == "fixture-gpu");
            var cpuHistory = model.Processors[(2, 3)];
            var diskHistory = diskRow.History;
            Require(cpuHistory.LastTime == time && cpuHistory.LastValue.Value == 10
                && diskHistory.LastTime == time && diskHistory.LastValue.Value == 20,
                "Definitions delivered before CPU/resource topology did not attach their retained points.");
            Require(gpuRow.Gpu!.Engines.Count == 0, "The late-engine fixture already contains an engine.");
            var sourceEngine = new GpuEngineInfo { Id = definitions[2].DetailId };
            ApplyTargets(true);
            gpuRow.Gpu.Apply(new GpuInfo { Engines = &sourceEngine, EngineCount = 1 }, gpuRow, time, false);
            model.ApplyCollectedHistory(default);
            var engineHistory = gpuRow.Gpu.Engines["engine-a"].History;
            Require(engineHistory.LastTime == time && engineHistory.LastValue.Value == 30,
                "A GPU engine added after its definition did not attach without another history batch.");

            definitions[3] = new() { Id = 4, Channel = HistoryChannel.Dedicated, ResourceId = definitions[2].ResourceId };
            points[3] = new() { Sequence = 4, TimeNs = time, Series = 4,
                Metric = new Metric { Value = 40, State = SampleState.Valid, Source = MetricSource.Dxgi } };
            model.ApplyCollectedHistory(new HistoryView { DefinitionCount = 1, Definitions = definitions + 3, PointCount = 1, Points = points + 3 });
            var dedicated = gpuRow.Gpu.Dedicated;
            Require(dedicated.LastValue.Value == 40, "A new definition did not bind an existing GPU model.");

            ApplyTargets(false); model.ApplyCollectedHistory(default);
            Require(!model.Processors.ContainsKey((2, 3)) && !model.Resources.Contains(diskRow) && !model.Resources.Contains(gpuRow),
                "The topology-removal fixture retained a removed target.");
            ApplyTargets(true);
            var nextDisk = model.Resources.Single(row => row.Id == "fixture-disk");
            var nextGpu = model.Resources.Single(row => row.Id == "fixture-gpu");
            nextGpu.Gpu!.Apply(new GpuInfo { Engines = &sourceEngine, EngineCount = 1 }, nextGpu, time, false);
            model.ApplyCollectedHistory(default);
            Require(!ReferenceEquals(nextDisk, diskRow) && !ReferenceEquals(nextGpu, gpuRow)
                && ReferenceEquals(nextDisk.History, diskHistory) && ReferenceEquals(model.Processors[(2, 3)], cpuHistory)
                && ReferenceEquals(nextGpu.Gpu.Engines["engine-a"].History, engineHistory) && ReferenceEquals(nextGpu.Gpu.Dedicated, dedicated),
                "Recreated CPU/resource/GPU targets lost their existing collected history references.");

            model.ApplyCollectedHistory(new HistoryView { LostPoints = 1, CaptureErrors = 2 });
            for (uint i = 0; i < 4; ++i) points[i] = new() { Sequence = i + 5, TimeNs = time + 2_000_000_000, Series = i + 1,
                Metric = new Metric { Value = (i + 1) * 100, State = SampleState.Valid, Source = MetricSource.PerformanceCounter } };
            model.ApplyCollectedHistory(new HistoryView { PointCount = 4, Points = points });
            Require(ReferenceEquals(nextDisk.History, diskHistory) && ReferenceEquals(model.Processors[(2, 3)], cpuHistory)
                && ReferenceEquals(nextGpu.Gpu.Engines["engine-a"].History, engineHistory) && ReferenceEquals(nextGpu.Gpu.Dedicated, dedicated)
                && cpuHistory.LastValue.Value == 100 && diskHistory.LastValue.Value == 200 && engineHistory.LastValue.Value == 300
                && dedicated.LastValue.Value == 400 && cpuHistory.At(time)?.Value.Value == 10
                && cpuHistory.At(time + 1_000_000_000) is null && model.HistoryWarning.Length > 0,
                "Appending without rebinding discarded values, reference identity or an explicit collection gap.");
            Require(!model.ApplyCollectedHistory(default) && cpuHistory.Count == 2 && diskHistory.Count == 2
                && engineHistory.Count == 2 && dedicated.Count == 2,
                "An empty history batch changed retained point counts.");
        }
        App.Trace("Collected history topology passed: late CPU/resource/engine, new definitions, recreation, reference reuse and explicit gaps");
    }
}

public sealed partial class MainWindow
{
    private void CheckHeldProcessRefresh()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var originalSelection = processList.SelectedItems.OfType<ProcessSlot>().Select(slot => slot.Row).ToArray();
        var originalPrimary = SelectedProcess;
        var originalView = view;
        bool originalHeld = model.Held;
        var scroll = Descendant<ScrollViewer>(processList);
        double offset = scroll?.VerticalOffset ?? 0;
        try {
            ShowView(false);
            foreach (bool reusedPid in new[] { false, true }) {
                using var source = new MonitorModel();
                var a = WinUiChecks.Process(10, 100, 50);
                var b = WinUiChecks.Process(20, 200, 25);
                WinUiChecks.ApplyProcesses(source, a, b); source.Sort(2);
                var original = source.VisibleProcesses.ToArray();
                var detach = PreserveProcessView(source);
                try {
                    source.VisibleProcesses.RefreshView();
                    processList.UpdateLayout();
                    SelectProcess(original[0], false); processList.SelectedItems.Add(slotsByRow[original[1]]);
                    Require(SelectedProcess == original[0] && processList.SelectedItems.Count == 2,
                        "Held fixture did not select live A as primary and B as secondary.");
                    var heldSlots = processSlots.ToArray();
                    int reordered = 0;
                    source.VisibleProcesses.CollectionChanged += (_, _) => ++reordered;
                    var changed = new List<string?>();
                    original[0].PropertyChanged += (_, args) => changed.Add(args.PropertyName);
                    source.SetHeld(true);
                    a.CpuPercent.Value = 5; a.WorkingSetBytes = 256 * 1024 * 1024;
                    var added = WinUiChecks.Process(reusedPid ? 20u : 30u, reusedPid ? 201u : 300u, 90);
                    WinUiChecks.ApplyProcesses(source, a, added);
                    var current = source.Processes.Single(row => row.Key.Pid == added.Pid);
                    Require(source.Held && source.VisibleProcesses.SequenceEqual(original) && reordered == 0
                        && processSlots.SequenceEqual(heldSlots) && heldSlots.Select(slot => slot.Row).SequenceEqual(original),
                        "A held native frame changed production process slots or their positions.");
                    Require(original[0].Cpu.Value == 5 && original[0].WorkingBytes == 256 * 1024 * 1024
                        && changed.Contains(nameof(ProcessRow.CpuText)) && changed.Contains(nameof(ProcessRow.WorkingText)),
                        "A held native frame stopped updating process values.");
                    Require(original[1].Ended && original[1].CpuText == Loc.T("Ukončeno") && !source.IsCurrent(original[1])
                        && source.IsCurrent(original[0]) && source.IsCurrent(current) && !source.VisibleProcesses.Contains(current),
                        "A held native frame lost the ended row or exposed its replacement.");
                    Require(SelectedProcess == original[0] && processList.SelectedItems.Count == 2
                        && processList.SelectedItems.Contains(slotsByRow[original[1]]) && !slotsByRow.ContainsKey(current),
                        "A held native frame changed the production primary or selected identities.");
                    source.SetHeld(false);
                    Require(!source.Held && source.VisibleProcesses.SequenceEqual(new[] { current, original[0] })
                        && processSlots.Select(slot => slot.Row).SequenceEqual(source.VisibleProcesses)
                        && current.Key.Generation == added.Generation && !current.Ended && !source.VisibleProcesses.Contains(original[1]),
                        "Releasing held rows did not restore current identities and CPU order in production slots.");
                    Require(SelectedProcess == original[0] && processList.SelectedItems.Count == 1
                        && processList.SelectedItems.Contains(slotsByRow[original[0]]) && !processList.SelectedItems.Contains(slotsByRow[current])
                        && !slotsByRow.ContainsKey(original[1]),
                        "Production selection lost live A or transferred ended B to a new identity after release.");
                } finally { detach(); }
            }
            App.Trace("Held production table refresh passed: values, ended rows, new/reused PIDs, selected live A and primary after release");
        } finally {
            model.VisibleProcesses.RefreshView();
            processList.SelectedItems.Clear();
            if (originalPrimary is not null) SelectProcess(originalPrimary, false);
            foreach (var row in originalSelection) if (row != originalPrimary && slotsByRow.TryGetValue(row, out var slot)) processList.SelectedItems.Add(slot);
            scroll?.ChangeView(null, offset, null, true);
            ShowView(originalView); model.SetHeld(originalHeld);
        }
    }
}
