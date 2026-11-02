using System.Collections.ObjectModel;
using System.ComponentModel;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ApplicationGroupRow(ProcessKey key) : INotifyPropertyChanged
{
    public ProcessKey Key { get; } = key;
    public string Title { get; private set; } = "";
    public string Summary { get; private set; } = "";
    internal ProcessRow[] Members { get; private set; } = [];
    internal ProcessApplicationInfo[] Windows { get; private set; } = [];
    private HashSet<ProcessKey> membership = [];
    internal bool Contains(ProcessKey key) => membership.Contains(key);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(ProcessRow root, ProcessApplicationInfo[] windows, ProcessRow[] members, bool partial)
    {
        Members = members; Windows = windows; membership = members.Select(row => row.Key).ToHashSet();
        string title = GlobalObjectSearchText.Flat(windows.FirstOrDefault(window => window.Title.Length > 0)?.Title ?? root.Name, 100);
        string tooltip = GlobalObjectSearchText.Flat(windows.FirstOrDefault(window => window.Title.Length > 0)?.Title ?? root.Name, 4096);
        bool changed = Title != title || TitleTooltip != tooltip || !ReferenceEquals(IconRow, root);
        Title = title; TitleTooltip = tooltip; IconRow = root;
        changed |= UpdatePresentation(partial);
        if (changed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
    internal void RefreshMetrics(bool partial)
    {
        if (UpdatePresentation(partial)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
    internal bool CaptionMatches(string query) => Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || TitleTooltip.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Windows.Any(window => window.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || window.AppId.Contains(query, StringComparison.OrdinalIgnoreCase) || window.Package.Contains(query, StringComparison.OrdinalIgnoreCase));
}

internal sealed partial class ApplicationGroupModel
{
    private readonly Dictionary<ProcessKey, ApplicationGroupRow> groups = [];
    private ProcessApplicationInfo[] inventory = [];
    private ProcessKey? selected;
    private long receivedAt;
    private bool partial, missingLinks;
    internal ObservableCollection<ApplicationGroupRow> Visible { get; } = [];
    internal ApplicationGroupRow? Selected => selected is { } key && groups.TryGetValue(key, out var group) && Visible.Contains(group) ? group : null;
    internal bool Updating { get; private set; }
    internal string Error { get; private set; } = "";
    internal bool HasInventory => receivedAt > 0;
    internal long ReceivedAt => receivedAt;
    internal bool Stale => HasInventory && NativeMonitor.MonotonicNowNs - receivedAt > 8_000_000_000;
    internal bool Partial => partial || missingLinks || Stale || Error.Length > 0;
    internal void Apply(ProcessDetailsSnapshot snapshot)
    {
        if (snapshot.State != ProcessDetailsState.Ok) { Error = "Inventář aplikací není dostupný: " + snapshot.State; return; }
        inventory = snapshot.Applications; partial = snapshot.PartialMetrics; receivedAt = NativeMonitor.MonotonicNowNs; Error = "";
    }
    internal void Failed(string error) => Error = error;
    internal void RefreshMetrics(bool held)
    {
        foreach (var group in groups.Values) group.RefreshMetrics(held || Partial);
        if (!held) ApplyVisible(Ordered(Visible));
    }
    internal void Select(ProcessKey key) { if (groups.ContainsKey(key)) selected = key; }
    internal ApplicationGroupRow? Containing(ProcessKey key) => groups.Values.FirstOrDefault(group => group.Contains(key));
    internal bool Contains(ProcessRow row) => Selected?.Contains(row.Key) == true;

    internal void Refresh(IReadOnlyCollection<ProcessRow> processes, string query)
    {
        Updating = true;
        try {
            var byKey = processes.ToDictionary(row => row.Key);
            missingLinks = inventory.Any(window => !byKey.ContainsKey(window.Owner) || window.Host is { } host && !byKey.ContainsKey(host));
            var byPid = processes.ToDictionary(row => row.Key.Pid);
            var windows = inventory.Where(window => byKey.ContainsKey(window.Owner)).GroupBy(window => window.Owner)
                .ToDictionary(group => group.Key, group => group.ToArray());
            foreach (var key in groups.Keys.Where(key => !windows.ContainsKey(key)).ToArray()) groups.Remove(key);
            foreach (var key in windows.Keys) if (!groups.ContainsKey(key)) groups.Add(key, new(key));
            var assigned = groups.ToDictionary(pair => pair.Key, pair => (ApplicationGroupRow?)pair.Value);
            // A frame host is charged only when this inventory ties it to exactly one application.
            foreach (var host in inventory.Where(window => window.Host.HasValue && groups.ContainsKey(window.Owner))
                .GroupBy(window => window.Host!.Value)) {
                var owners = host.Select(window => window.Owner).Distinct().ToArray();
                if (owners.Length == 1 && byKey.ContainsKey(host.Key) && !assigned.ContainsKey(host.Key)) assigned.Add(host.Key, groups[owners[0]]);
            }
            foreach (var row in processes) {
                if (assigned.ContainsKey(row.Key)) continue;
                var path = new List<ProcessRow>(); var visited = new HashSet<ProcessKey>();
                var current = row; ApplicationGroupRow? owner = null;
                while (visited.Add(current.Key)) {
                    if (assigned.TryGetValue(current.Key, out owner)) break;
                    path.Add(current);
                    if (!byPid.TryGetValue(current.ParentPid, out var parent) || !ProcessTree.IsVerifiedParent(current, parent)) break;
                    current = parent;
                }
                foreach (var member in path) assigned[member.Key] = owner;
            }
            var members = groups.Keys.ToDictionary(key => key, _ => new List<ProcessRow>());
            foreach (var row in processes) if (assigned.TryGetValue(row.Key, out var group) && group is not null) members[group.Key].Add(row);
            foreach (var (key, group) in groups) group.Update(byKey[key], windows[key], members[key].ToArray(), Partial);
            var visible = Ordered(groups.Values.Where(group => query.Length == 0 || group.CaptionMatches(query)
                || group.Members.Any(row => row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || row.PidText.Contains(query, StringComparison.Ordinal))));
            if (selected is not { } selectedKey || !visible.Any(group => group.Key == selectedKey)) selected = visible.FirstOrDefault()?.Key;
            ApplyVisible(visible);
        } finally { Updating = false; }
    }
}
