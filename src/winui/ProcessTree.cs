using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static class ProcessTree
{
    public static bool IsVerifiedParent(ProcessRow child, ProcessRow parent) => child.ParentPid == parent.Key.Pid && child.Key.Pid != parent.Key.Pid
        && !child.Key.Boot.IsEmpty && child.Key.Boot == parent.Key.Boot && child.Created100ns != 0 && parent.Created100ns != 0
        && parent.Created100ns <= child.Created100ns;

    internal static bool HasVerifiedIdentity(ProcessRow row) => !row.Ended && !row.Key.Boot.IsEmpty && row.Created100ns != 0
        && row.Key.Generation != 0 && row.Key.Kind is GenerationKind.CreationTime or GenerationKind.SequenceNumber;

    internal static int[] ParentIndices(IReadOnlyList<ProcessRow> rows, out bool[] incomplete)
    {
        incomplete = new bool[rows.Count];
        var parents = new int[rows.Count]; Array.Fill(parents, -1);
        var byPid = new Dictionary<(BootIdentifier Boot, uint Pid), int>();
        for (int i = 0; i < rows.Count; ++i) {
            var row = rows[i]; incomplete[i] = !HasVerifiedIdentity(row);
            var key = (row.Key.Boot, row.Key.Pid);
            if (byPid.TryGetValue(key, out int previous)) {
                if (previous >= 0) incomplete[previous] = true;
                incomplete[i] = true; byPid[key] = -1;
            } else byPid.Add(key, i);
        }
        var children = new int[rows.Count];
        for (int i = 0; i < rows.Count; ++i) {
            var row = rows[i];
            if (row.ParentPid == 0) continue;
            if (!byPid.TryGetValue((row.Key.Boot, row.ParentPid), out int parent) || parent < 0) {
                incomplete[i] = true; continue;
            }
            if (HasVerifiedIdentity(row) && HasVerifiedIdentity(rows[parent]) && IsVerifiedParent(row, rows[parent])) {
                parents[i] = parent; ++children[parent];
            } else { incomplete[i] = true; incomplete[parent] = true; }
        }
        var leaves = new Queue<int>();
        for (int i = 0; i < children.Length; ++i) if (children[i] == 0) leaves.Enqueue(i);
        while (leaves.TryDequeue(out int child)) {
            int parent = parents[child];
            if (parent >= 0 && --children[parent] == 0) leaves.Enqueue(parent);
        }
        // Equal raw creation times can form a cycle. Reject every cycle edge, independent of row order.
        for (int i = 0; i < children.Length; ++i) if (children[i] != 0) {
            parents[i] = -1; incomplete[i] = true;
        }
        return parents;
    }

    public static List<ProcessRow> Order(IReadOnlyList<ProcessRow> rows, IReadOnlySet<ProcessKey> collapsed)
    {
        var parents = ParentIndices(rows, out _);
        var children = new Dictionary<ProcessKey, List<ProcessRow>>(); var roots = new List<ProcessRow>();
        for (int i = 0; i < rows.Count; ++i) {
            var row = rows[i];
            if (parents[i] >= 0) {
                var parent = rows[parents[i]];
                if (!children.TryGetValue(parent.Key, out var list)) children.Add(parent.Key, list = []);
                list.Add(row);
            } else roots.Add(row);
        }
        var visible = new List<ProcessRow>(); var visited = new HashSet<ProcessKey>();
        void Visit(ProcessRow root)
        {
            var pending = new Stack<(ProcessRow Row, int Depth, bool Visible)>();
            pending.Push((root, 0, true));
            while (pending.TryPop(out var entry)) {
                var row = entry.Row;
                if (!visited.Add(row.Key)) continue;
                bool branch = children.TryGetValue(row.Key, out var list);
                bool closed = collapsed.Contains(row.Key);
                row.SetTree(Math.Min(12, entry.Depth), branch, closed);
                if (entry.Visible) visible.Add(row);
                if (branch) for (int i = list!.Count - 1; i >= 0; --i)
                    pending.Push((list[i], entry.Depth + 1, entry.Visible && !closed));
            }
        }
        foreach (var row in roots) Visit(row);
        foreach (var row in rows) if (!visited.Contains(row.Key)) Visit(row);
        return visible;
    }
}
