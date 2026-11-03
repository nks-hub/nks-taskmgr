using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    internal static void ProcessBranchValues()
    {
        BranchFilteringAndUpdates();
        BranchVerifiedIdentities();
        BranchIncompleteMetrics();
        BranchCyclesAndDepth();
        App.Trace("Process branch values passed: full-tree sums, filtering, held updates, own values, verified identities, partial data and bounded cycles/depth");
    }

    private static ProcessInfo BranchProcess(uint pid, ulong generation, double cpu, uint parent = 0)
    {
        var row = Process(pid, generation, cpu, parent);
        row.WorkingSetBytes = 1048576; row.PrivateBytes = 2097152;
        row.ReadIoBytesPerSecond = new Metric { Value = 1048576, State = SampleState.Valid };
        row.WriteIoBytesPerSecond = new Metric { Value = 2097152, State = SampleState.Valid };
        return row;
    }

    private static unsafe ProcessRow BranchRow(ProcessInfo source, ulong created, BootIdentifier? boot = null)
    {
        var row = new ProcessRow(new(boot ?? new BootIdentifier(1, 2), source.Pid, source.GenerationKind, source.Generation));
        row.Update(source, false); row.SetCreatedTime(created); return row;
    }

    private static void BranchFilteringAndUpdates()
    {
        using var model = new MonitorModel();
        var parent = BranchProcess(10, 1000, 5);
        var child = BranchProcess(20, 2000, 7, 10);
        var grandchild = BranchProcess(30, 3000, 3, 20);
        var other = BranchProcess(40, 4000, 12);
        ApplyProcesses(model, parent, child, grandchild, other);
        model.SetMode(ProcessMode.Tree); model.SetTableValueMode(ProcessTableValueMode.ProcessWithDescendants);
        var root = model.Processes.Single(row => row.Key.Pid == 10);
        Require(root.BranchTotal.Members == 3 && root.BranchTotal.Cpu.Value == 15 && root.BranchTotal.Cpu.Known == 3
            && root.BranchTotal.Working.Value == 3145728 && root.BranchTotal.Private.Value == 6291456
            && root.BranchTotal.Read.Value == 3145728 && root.BranchTotal.Write.Value == 6291456
            && root.Cpu.Value == 5 && root.WorkingBytes == 1048576 && root.PrivateBytes == 2097152
            && root.ReadIo.Value == 1048576 && root.WriteIo.Value == 2097152 && root.TableCpuText != root.CpuText,
            "The branch total omitted a current member, counted it twice or replaced the PID's own metrics.");
        model.Toggle(root);
        Require(model.VisibleProcesses.Count == 2 && root.BranchTotal.Members == 3 && root.BranchTotal.Cpu.Value == 15,
            "Collapsing a branch removed hidden descendants from its total.");
        model.SetQuery("10");
        Require(model.VisibleProcesses.Count == 1 && ReferenceEquals(model.VisibleProcesses[0], root)
            && root.BranchTotal.Members == 3 && root.BranchTotal.Cpu.Value == 15,
            "Searching a parent computed its total from the filtered display list.");
        model.SetQuery(""); model.Toggle(root); model.Sort(2);
        Require(ReferenceEquals(model.VisibleProcesses[0], root), "Tree sorting used the parent's own CPU instead of the displayed branch CPU.");
        int notifications = 0;
        root.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ProcessRow.TableCpuText)) ++notifications; };
        model.SetHeld(true); var heldOrder = model.VisibleProcesses.ToArray();
        child.CpuPercent.Value = 1; grandchild.CpuPercent.Value = 1;
        ApplyProcesses(model, parent, child, grandchild, other);
        Require(root.BranchTotal.Cpu.Value == 7 && notifications != 0 && heldOrder.SequenceEqual(model.VisibleProcesses)
            && ReferenceEquals(root, model.Processes.Single(row => row.Key.Pid == 10)) && root.Cpu.Value == 5,
            "A child-only change left its held ancestor stale or replaced/reordered held row identities.");
        model.SetHeld(false);
        Require(model.VisibleProcesses[0].Key.Pid == 40, "Releasing held order did not sort by the current branch CPU.");
        model.SetTableValueMode(ProcessTableValueMode.Own);
        Require(root.TableCpuText == root.CpuText && root.TableWorkingText == root.WorkingText
            && root.TablePrivateText == root.PrivateText && root.TableReadText == root.ReadText && root.TableWriteText == root.WriteText,
            "Returning to own values changed existing PID text semantics.");
        model.SetTableValueMode(ProcessTableValueMode.ProcessWithDescendants); model.SetMode(ProcessMode.All);
        Require(root.TableCpuText == root.CpuText && model.TableValueMode == ProcessTableValueMode.ProcessWithDescendants,
            "A saved branch preference changed flat process values.");
        var ownChanges = new HashSet<string?>();
        root.PropertyChanged += (_, args) => ownChanges.Add(args.PropertyName);
        parent.CpuPercent.Value = 6; parent.WorkingSetBytes = 2097152; parent.PrivateBytes = 3145728;
        parent.ReadIoBytesPerSecond.Value = 2097152; parent.WriteIoBytesPerSecond.Value = 3145728;
        ApplyProcesses(model, parent, child, grandchild, other);
        Require(root.TableCpuText == root.CpuText && root.TableWorkingText == root.WorkingText && root.TablePrivateText == root.PrivateText
            && root.TableReadText == root.ReadText && root.TableWriteText == root.WriteText
            && new[] { nameof(ProcessRow.TableCpuText), nameof(ProcessRow.TableWorkingText), nameof(ProcessRow.TablePrivateText),
                nameof(ProcessRow.TableReadText), nameof(ProcessRow.TableWriteText) }.All(ownChanges.Contains),
            "Own metric updates failed to notify the five table bindings after leaving branch mode.");
        model.SetMode(ProcessMode.Tree);
        Require(root.BranchTotal.Cpu.Value == 8 && root.TableCpuText != root.CpuText, "Returning to tree mode lost current branch values.");
    }

    private static void BranchVerifiedIdentities()
    {
        using var model = new MonitorModel();
        var parent = BranchProcess(10, 9000, 2); parent.GenerationKind = GenerationKind.SequenceNumber;
        var child = BranchProcess(20, 7, 3, 10); child.GenerationKind = GenerationKind.SequenceNumber;
        var creation = new[] {
            new ProcessCreationInfo { Pid = 10, Kind = parent.GenerationKind, Generation = 9000, Created100ns = 100, State = SampleState.Valid },
            new ProcessCreationInfo { Pid = 20, Kind = child.GenerationKind, Generation = 7, Created100ns = 200, State = SampleState.Valid }
        };
        ApplyProcessFixture(model, [parent, child], creation);
        model.SetMode(ProcessMode.Tree); model.SetTableValueMode(ProcessTableValueMode.ProcessWithDescendants);
        var root = model.Processes.Single(row => row.Key.Pid == 10);
        Require(root.BranchTotal.Members == 2 && root.BranchTotal.Cpu.Value == 5,
            "Sequence ordinals were treated as creation times when summing a verified branch.");
        creation[1].Generation = 8;
        ApplyProcessFixture(model, [parent, child], creation);
        Require(root.BranchTotal.Members == 1 && root.BranchTotal.Incomplete && root.BranchTotal.Cpu.Value == 2
            && model.Processes.Single(row => row.Key.Pid == 20).Created100ns == 0,
            "Creation metadata for another PID lifetime joined an unverified descendant to the sum.");
        creation[1].Generation = 7; creation[1].Created100ns = 50;
        ApplyProcessFixture(model, [parent, child], creation);
        Require(root.BranchTotal.Members == 1 && root.BranchTotal.Incomplete, "A younger reused parent PID included an older child.");
        var a = BranchRow(BranchProcess(10, 100, 2), 100);
        var b = BranchRow(BranchProcess(20, 200, 3, 10), 200);
        var c = BranchRow(BranchProcess(10, 100, 50), 100, new BootIdentifier(3, 4));
        var d = BranchRow(BranchProcess(20, 200, 70, 10), 200, new BootIdentifier(3, 4));
        var totals = ProcessBranchTotals.Calculate([a, b, c, d]);
        Require(totals[0].Cpu.Value == 5 && totals[0].Members == 2 && totals[2].Cpu.Value == 120 && totals[2].Members == 2
            && ProcessTree.Order([a, b, c, d], new HashSet<ProcessKey>()).Count == 4,
            "Equal PIDs in different boots were combined or broke current process-tree projection.");
        var missing = BranchProcess(30, 0, 99, 10); missing.GenerationKind = GenerationKind.Missing;
        totals = ProcessBranchTotals.Calculate([a, BranchRow(missing, 0)]);
        Require(totals[0].Members == 1 && totals[0].Incomplete && totals[1].Cpu.Known == 0,
            "A missing lifetime identity contributed a supposedly verified branch sample.");
    }

    private static void BranchIncompleteMetrics()
    {
        var first = BranchProcess(10, 100, 0);
        first.WorkingSetBytes = first.PrivateBytes = 0;
        first.ReadIoBytesPerSecond.Value = first.WriteIoBytesPerSecond.Value = 0;
        var second = BranchProcess(20, 200, 99, 10);
        second.CpuPercent.State = SampleState.Unavailable;
        second.ReadIoBytesPerSecond.Value = double.NaN; second.WriteIoBytesPerSecond.Value = 0;
        var root = BranchRow(first, 100); var missing = BranchRow(second, 200);
        var stale = BranchRow(BranchProcess(30, 300, 50, 10), 300); stale.SetStatus(false, true, false);
        var ended = BranchRow(BranchProcess(40, 400, 70, 10), 400); ended.SetStatus(true, false, false);
        var totals = ProcessBranchTotals.Calculate([root, missing, stale, ended]);
        root.SetBranchValues(totals[0], true, false);
        Require(totals[0].Members == 3 && totals[0].Incomplete && totals[0].Cpu.Known == 1 && totals[0].Cpu.Value == 0
            && totals[0].Working.Known == 2 && totals[0].Read.Known == 1 && totals[0].Write.Known == 2
            && root.TableCpuText.StartsWith("≥ ", StringComparison.Ordinal) && root.TableMetricsDescription.Contains("CPU 1/3", StringComparison.Ordinal),
            "Unavailable, stale or ended members became fresh zero samples or hid the partial known-member count.");
        totals = ProcessBranchTotals.Calculate([root]); root.SetBranchValues(totals[0], true, false);
        Require(totals[0].Cpu.Known == 1 && !totals[0].Incomplete && !root.TableCpuText.StartsWith("≥ ", StringComparison.Ordinal)
            && root.TableWorkingText == root.WorkingText, "A valid zero metric became unavailable or partial.");
        second.ParentPid = 0; second.ReadIoBytesPerSecond.State = SampleState.First;
        var unavailable = BranchRow(second, 200); totals = ProcessBranchTotals.Calculate([unavailable]);
        unavailable.SetBranchValues(totals[0], true, false);
        Require(totals[0].Cpu.Known == 0 && unavailable.TableCpuText == "Měření nedostupné"
            && unavailable.TableReadText == "Měření nedostupné", "A wholly unknown metric was displayed as a numeric branch sum.");
        first.WorkingSetBytes = ulong.MaxValue; first.CpuPercent.Value = double.MaxValue;
        second.ParentPid = 10; second.WorkingSetBytes = 1; second.CpuPercent.State = SampleState.Valid; second.CpuPercent.Value = double.MaxValue;
        root = BranchRow(first, 100); var overflow = BranchRow(second, 200);
        totals = ProcessBranchTotals.Calculate([root, overflow]); root.SetBranchValues(totals[0], true, false);
        Require(totals[0].Working.Overflow && totals[0].Cpu.Overflow && root.TableWorkingText == "Přetečení součtu"
            && root.TableCpuText == "Přetečení součtu" && double.IsNegativeInfinity(totals[0].Cpu.SortValue),
            "Byte or floating-point overflow produced a wrapped or infinite branch value.");
        second.CpuPercent.Value = -1; second.ReadIoBytesPerSecond.State = SampleState.Valid;
        second.ReadIoBytesPerSecond.Value = double.PositiveInfinity;
        totals = ProcessBranchTotals.Calculate([BranchRow(second, 200)]);
        Require(totals[0].Cpu.Known == 0 && totals[0].Read.Known == 0, "Negative or non-finite samples entered a known branch total.");
    }

    private static void BranchCyclesAndDepth()
    {
        var a = BranchRow(BranchProcess(10, 100, 1, 20), 100);
        var b = BranchRow(BranchProcess(20, 100, 2, 10), 100);
        var leaf = BranchRow(BranchProcess(30, 200, 3, 10), 200);
        var totals = ProcessBranchTotals.Calculate([a, b, leaf]);
        var reverse = ProcessBranchTotals.Calculate([leaf, b, a]);
        Require(totals[0].Incomplete && totals[1].Incomplete && totals[0].Members == 2 && totals[0].Cpu.Value == 4
            && totals[1].Members == 1 && totals[1].Cpu.Value == 2 && totals[0] == reverse[2] && totals[1] == reverse[1],
            "An equal-time parent cycle counted a member repeatedly or changed totals with sort order.");
        var floating = new[] { BranchRow(BranchProcess(1, 100, 0), 100), BranchRow(BranchProcess(2, 200, 1e16, 1), 200),
            BranchRow(BranchProcess(3, 300, 1, 1), 300), BranchRow(BranchProcess(4, 400, 1, 1), 400) };
        Require(ProcessBranchTotals.Calculate(floating)[0] == ProcessBranchTotals.Calculate(floating.Reverse().ToArray())[^1],
            "Floating-point branch addition changed with input sorting.");
        var rows = new ProcessRow[10_000];
        for (int i = 0; i < rows.Length; ++i) rows[i] = BranchRow(BranchProcess((uint)i + 1, (ulong)i + 1000, 1, (uint)i), (ulong)i + 1000);
        totals = ProcessBranchTotals.Calculate(rows);
        reverse = ProcessBranchTotals.Calculate(rows.Reverse().ToArray());
        Require(totals[0].Members == rows.Length && totals[0].Cpu.Value == rows.Length && totals[0].Cpu.Known == rows.Length
            && !totals[0].Incomplete && totals[0] == reverse[^1] && totals[^1].Members == 1,
            "A deep verified chain lost/doubled members or depended on input sorting.");
        var visible = ProcessTree.Order(rows, new HashSet<ProcessKey> { rows[0].Key });
        Require(visible.Count == 1 && ReferenceEquals(visible[0], rows[0]) && totals[0].Members == rows.Length,
            "A collapsed deep tree failed to terminate or changed its full branch membership.");
    }
}
