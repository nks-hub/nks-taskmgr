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
                    Require(original[1].Ended && original[1].CpuText == "Ukončeno" && !source.IsCurrent(original[1])
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
