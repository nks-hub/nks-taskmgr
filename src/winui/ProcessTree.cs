namespace NksTaskMgr.WinUI;

public static class ProcessTree
{
    public static List<ProcessRow> Order(IReadOnlyList<ProcessRow> rows, IReadOnlySet<ProcessKey> collapsed)
    {
        var byPid = rows.ToDictionary(row => row.Key.Pid);
        var children = new Dictionary<ProcessKey, List<ProcessRow>>(); var roots = new List<ProcessRow>();
        foreach (var row in rows) {
            if (row.ParentPid != row.Key.Pid && byPid.TryGetValue(row.ParentPid, out var parent)
                && row.Key.Kind == Native.GenerationKind.CreationTime && parent.Key.Kind == Native.GenerationKind.CreationTime
                && row.Key.Boot == parent.Key.Boot && parent.Key.Generation <= row.Key.Generation) {
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
