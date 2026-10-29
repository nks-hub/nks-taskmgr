using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static class WinUiChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static void ModelInvariants(MonitorModel model)
    {
        Require(!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported, "WinUI probe is not Native AOT.");
        var own = model.Processes.Single(row => row.Key.Pid == Environment.ProcessId);
        Require(own.Key.Kind == GenerationKind.CreationTime && own.Key.Generation != 0, "WinUI lost native process identity.");
        model.SetHeld(true); model.SetQuery(own.PidText);
        Require(!model.Held && model.VisibleProcesses.Any(row => row.Key == own.Key)
            && model.VisibleProcesses.All(row => row.PidText.Contains(own.PidText) || row.Name.Contains(own.PidText, StringComparison.OrdinalIgnoreCase)),
            "Held rows prevented explicit search.");
        model.SetQuery(string.Empty); model.SetMode(ProcessMode.Tree);
        Require(model.VisibleProcesses.Count == model.Processes.Count, "Expanded WinUI tree lost a process.");
        var parent = model.VisibleProcesses.FirstOrDefault(row => row.HasChildren);
        if (parent is not null) {
            model.SetHeld(true); model.Toggle(parent);
            Require(!model.Held && parent.Collapsed, "Held rows prevented branch collapse.");
            model.Toggle(parent);
        }
        model.SetMode(ProcessMode.All); model.SetHeld(true); model.Sort(2);
        Require(!model.Held, "Held rows prevented explicit sorting.");
        Require(Display.Metric(new Metric { State = SampleState.Unavailable }, " GiB") == "Měření nedostupné",
            "Unavailable measurement became a numeric value.");
        Require(Display.Metric(new Metric { State = SampleState.Valid, Value = 0 }, " %") == "0,0 %", "Measured zero was lost.");
        Require(model.TopConsumers(false).All(row => row.Key.Pid != 0 && row.Cpu.State == SampleState.Valid), "Idle time was shown as CPU consumption.");
        DeepTree();
    }

    private static void DeepTree()
    {
        var rows = new List<ProcessRow>();
        for (uint i = 1; i <= 10_000; ++i) {
            var row = new ProcessRow(new ProcessKey(1, i, GenerationKind.CreationTime, i));
            row.Update(new ProcessInfo { ParentPid = i - 1 }); rows.Add(row);
        }
        var expanded = ProcessTree.Order(rows, new HashSet<ProcessKey>());
        Require(expanded.Count == 10_000 && expanded[^1].Depth == 12, "Deep tree failed or exceeded the indentation limit.");
        Require(ProcessTree.Order(rows, new HashSet<ProcessKey> { rows[0].Key }).Count == 1, "Collapsed deep tree leaked descendants.");
        var a = new ProcessRow(new ProcessKey(1, 1, GenerationKind.CreationTime, 10));
        var b = new ProcessRow(new ProcessKey(1, 2, GenerationKind.CreationTime, 10));
        a.Update(new ProcessInfo { ParentPid = 2 }); b.Update(new ProcessInfo { ParentPid = 1 });
        Require(ProcessTree.Order(new[] { a, b }, new HashSet<ProcessKey>()).Count == 2, "A parent cycle lost a process or hung the UI.");
    }
}
