using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MonitorModel
{
    internal string ProcessQuery => query;
    internal ProcessKey[] NavigationCollapsed => collapsed.ToArray();

    internal void RestoreProcessNavigation(ProcessMode mode, string text, int column, bool reverse, ProcessKey? application, ProcessKey[]? closed = null)
    {
        held = false; Mode = mode; query = text; sortColumn = column; descending = reverse;
        if (closed is not null) { collapsed.Clear(); foreach (var key in closed) collapsed.Add(key); }
        if (mode == ProcessMode.Applications && application is { } applicationKey) Applications.Select(applicationKey);
        Rebuild(true);
    }
}
