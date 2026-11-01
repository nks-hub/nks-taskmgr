using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static void ProcessRefresh()
    {
        UnchangedProcessTree();
        App.Trace("Process tree refresh passed: unchanged expanded/collapsed trees, search and flat mode");
    }

    internal static ProcessInfo Process(uint pid, ulong generation, double cpu, uint parent = 0) => new() {
        Pid = pid, ParentPid = parent, Generation = generation, GenerationKind = GenerationKind.CreationTime,
        CpuPercent = new Metric { Value = cpu, State = SampleState.Valid, Source = MetricSource.NativeProcess }
    };

    internal static unsafe void ApplyProcesses(MonitorModel model, params ProcessInfo[] rows)
    {
        fixed (ProcessInfo* data = rows) model.Apply(new FrameView {
            Flags = FrameFlags.ProcessData,
            Processes = new ProcessData { MeasuredAtNs = NativeMonitor.MonotonicNowNs,
                Processes = data, ProcessCount = (uint)rows.Length, LogicalProcessors = 1 }
        });
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
