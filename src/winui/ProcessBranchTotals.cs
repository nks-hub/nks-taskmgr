namespace NksTaskMgr.WinUI;

internal static class ProcessBranchTotals
{
    internal static ProcessBranchTotal[] Calculate(IReadOnlyList<ProcessRow> rows)
    {
        var parents = ProcessTree.ParentIndices(rows, out var incomplete);
        var totals = new ProcessBranchTotal[rows.Count];
        var children = new int[rows.Count];
        for (int i = 0; i < rows.Count; ++i) {
            totals[i] = ProcessBranchTotal.Own(rows[i], incomplete[i]);
            if (parents[i] >= 0) ++children[parents[i]];
        }
        // A full-key order also makes floating-point addition independent of display sorting.
        var leaves = new PriorityQueue<int, (ulong, ulong, uint, uint, ulong)>();
        void Enqueue(int index) {
            var key = rows[index].Key;
            leaves.Enqueue(index, (key.Boot.Low, key.Boot.High, key.Pid, (uint)key.Kind, key.Generation));
        }
        for (int i = 0; i < rows.Count; ++i) if (children[i] == 0) Enqueue(i);
        while (leaves.TryDequeue(out int child, out _)) {
            int parent = parents[child];
            if (parent < 0) continue;
            totals[parent] = totals[parent].Add(totals[child]);
            if (--children[parent] == 0) Enqueue(parent);
        }
        return totals;
    }
}

public sealed partial class MonitorModel
{
    private bool branchTotalsDirty = true, branchValuesShown;
    public ProcessTableValueMode TableValueMode { get; private set; }
    private bool BranchValuesEnabled => Mode == ProcessMode.Tree && TableValueMode == ProcessTableValueMode.ProcessWithDescendants;
    private bool BranchSort => BranchValuesEnabled && sortColumn is >= 2 and <= 6;
    private double BranchSortValue(ProcessBranchTotal total) => sortColumn switch {
        2 => total.Cpu.SortValue, 3 => total.Working.SortValue, 4 => total.Private.SortValue,
        5 => total.Read.SortValue, 6 => total.Write.SortValue, _ => throw new InvalidOperationException("Branch sort requires a metric column.")
    };

    public void SetTableValueMode(ProcessTableValueMode value)
    {
        if (value is not (ProcessTableValueMode.Own or ProcessTableValueMode.ProcessWithDescendants))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (TableValueMode == value) return;
        held = false; TableValueMode = value; Rebuild(true);
    }

    private bool RefreshProcessBranchValues()
    {
        if (!BranchValuesEnabled) {
            if (branchValuesShown) foreach (var row in processes.Values) row.SetBranchValues(row.BranchTotal, false, visualUpdates);
            branchValuesShown = false; return false;
        }
        if (!branchTotalsDirty && branchValuesShown) return false;
        bool orderChanged = false;
        if (branchTotalsDirty) {
            var rows = processes.Values.ToArray();
            var totals = ProcessBranchTotals.Calculate(rows);
            for (int i = 0; i < rows.Length; ++i) {
                if (BranchSort) orderChanged |= BranchSortValue(rows[i].BranchTotal) != BranchSortValue(totals[i]);
                rows[i].SetBranchValues(totals[i], true, visualUpdates);
            }
            branchTotalsDirty = false;
        } else foreach (var row in processes.Values) row.SetBranchValues(row.BranchTotal, true, visualUpdates);
        branchValuesShown = true; return orderChanged;
    }
}
