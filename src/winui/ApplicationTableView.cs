using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;
using Windows.System;

namespace NksTaskMgr.WinUI;

/// <summary>
/// Applications as one table like Task Manager: a row per application with totals over all its processes,
/// expandable to the processes with their own values. Selection drives the shared process commands.
/// </summary>
public sealed partial class MainWindow
{
    private readonly Grid appsPane = new() { Visibility = Visibility.Collapsed };
    private readonly ListView appsList = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0) };
    private readonly GroupLines appItems = [];
    private readonly List<(Button Button, TextBlock Text)> appHeaders = [];
    private readonly HashSet<ProcessKey> expandedApps = [];
    private readonly Dictionary<ProcessKey, GroupLine> appGroupItems = [];
    private readonly Dictionary<ProcessKey, GroupLine> appProcessItems = [];
    private Grid appsHeader = new();
    private int appSortColumn;
    private bool appSortDescending, updatingAppSelection, appNetworkShown = true;

    private void BuildApplicationTable()
    {
        appsPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); appsPane.RowDefinitions.Add(new RowDefinition());
        string[] tips = [
            Loc.T("Aplikace podle ověřených oken a počet jejích procesů. Šipka nebo dvojklik rozbalí procesy."), "",
            Loc.T("Součet CPU všech procesů aplikace. ≥ označuje známou část, když některý proces nemá aktuální vzorek."),
            Loc.T("Součet pracovních sad všech procesů aplikace. Přesné hodnoty v MB jsou v popisku buňky."),
            Loc.T("Součet čtení a zápisu všech procesů aplikace."), Loc.T("Odeslané a přijaté bajty TCP/UDP. Měří se jen během vlastního záznamu ETW se správcem.")];
        appsHeader = GroupHeader(Loc.T("Aplikace"), tips, appHeaders, column => {
            appSortDescending = appSortColumn == column ? !appSortDescending : column >= 2; appSortColumn = column; RefreshAppTable();
        });
        appsHeader.ColumnDefinitions[1].Width = new GridLength(0);
        appsPane.Children.Add(appsHeader);
        ConfigureGroupList(appsList, appItems, () => appNetworkShown);
        appsList.ContainerContentChanging += (_, args) => {
            if (!args.InRecycleQueue && args.ItemContainer.ContentTemplateRoot is Grid grid && grid.ColumnDefinitions.Count == GroupColumns.Length)
                grid.ColumnDefinitions[1].Width = new GridLength(0);
        };
        appsList.SelectionChanged += (_, _) => {
            if (updatingAppSelection || appsList.SelectedItem is not GroupLine { Group: ProcessKey key } line) return;
            SelectApplicationLine(key, line.Process);
        };
        appsList.DoubleTapped += (_, args) => { if (LineFrom(args.OriginalSource) is { IsGroup: true } line) ToggleAppGroup(line); };
        appsList.PreviewKeyDown += (_, args) => {
            if (args.Key is not (VirtualKey.Left or VirtualKey.Right) || appsList.SelectedItem is not GroupLine { IsGroup: true, Group: ProcessKey key }) return;
            if (args.Key == VirtualKey.Right) expandedApps.Add(key); else expandedApps.Remove(key);
            RefreshAppTable(); args.Handled = true;
        };
        AutomationProperties.SetName(appsList, Loc.T("Aplikace a jejich procesy")); AutomationProperties.SetAutomationId(appsList, "applications-list");
        Grid.SetRow(appsList, 1); appsPane.Children.Add(appsList);
    }

    private void ToggleAppGroup(GroupLine line)
    {
        if (line.Group is not ProcessKey key) return;
        if (!expandedApps.Add(key)) expandedApps.Remove(key);
        RefreshAppTable();
    }

    private void SelectApplicationLine(ProcessKey group, ProcessRow? process)
    {
        if (model.Applications.Visible.FirstOrDefault(row => row.Key == group) is not { } application) return;
        if (model.Applications.Selected?.Key != group) { model.SelectApplication(group); pendingApplicationSelection = null; RefreshProcesses(); }
        var target = process is not null && model.VisibleProcesses.Contains(process) ? process : model.VisibleProcesses.FirstOrDefault(row => row.Key == group);
        autoSelectedApplication = application.Key;
        if (target is not null && !ReferenceEquals(SelectedProcess, target)) SelectProcess(target, false);
    }

    private void RefreshAppTable()
    {
        if (model.Mode != ProcessMode.Applications) return;
        bool network = model.NetworkAvailable;
        if (network != appNetworkShown) {
            appNetworkShown = network;
            appsHeader.ColumnDefinitions[^1].Width = new GridLength(network ? GroupColumns[^1] : 0);
            appItems.Sync([]);
        }
        UpdateGroupHeader(appHeaders, Loc.T("Aplikace"), appSortColumn, appSortDescending);
        var groups = model.Applications.Visible.ToList();
        var lines = groups.Select(AppGroupLine).ToList();
        lines.Sort((a, b) => GroupLine.Compare(a, b, appSortColumn, appSortDescending));
        var wanted = new List<GroupLine>();
        var liveGroups = new HashSet<ProcessKey>(); var liveProcesses = new HashSet<ProcessKey>();
        foreach (var line in lines) {
            wanted.Add(line);
            var key = (ProcessKey)line.Group; liveGroups.Add(key);
            if (!expandedApps.Contains(key) || groups.FirstOrDefault(group => group.Key == key) is not { } application) continue;
            // Processes nest under their parent; siblings keep name and PID order so the live values do not reshuffle the rows.
            var members = application.Members.Where(row => !row.Ended).OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Key.Pid).ToArray();
            foreach (var (row, depth) in ProcessTree.Nested(members)) {
                liveProcesses.Add(row.Key);
                var child = AppProcessLine(key, row); child.SetDepth(depth); wanted.Add(child);
            }
        }
        foreach (var key in appGroupItems.Keys.Where(key => !liveGroups.Contains(key)).ToArray()) appGroupItems.Remove(key);
        foreach (var key in appProcessItems.Keys.Where(key => !liveProcesses.Contains(key)).ToArray()) appProcessItems.Remove(key);
        expandedApps.RemoveWhere(key => !liveGroups.Contains(key));
        updatingAppSelection = true;
        try {
            appItems.Sync(wanted);
            GroupLine? selection = null;
            if (SelectedProcess is { } selected && model.Applications.Selected is { } current) {
                selection = appProcessItems.TryGetValue(selected.Key, out var processLine) && wanted.Contains(processLine) ? processLine
                    : appGroupItems.GetValueOrDefault(current.Key);
            }
            if (!ReferenceEquals(appsList.SelectedItem, selection)) appsList.SelectedItem = selection;
        } finally { updatingAppSelection = false; }
    }

    private GroupLine AppGroupLine(ApplicationGroupRow group)
    {
        if (!appGroupItems.TryGetValue(group.Key, out var line) || !ReferenceEquals(line.IconRow, group.IconRow)) {
            appGroupItems[group.Key] = line = new GroupLine(group.Key, null, group.IconRow) { Toggle = ToggleAppGroup };
        }
        var live = group.Members.Where(row => !row.Ended && !row.Stale).ToArray();
        var cpu = live.Where(row => row.Cpu.State == SampleState.Valid && double.IsFinite(row.Cpu.Value)).ToArray();
        double cpuValue = cpu.Sum(row => row.Cpu.Value);
        ulong working = 0, privateBytes = 0; foreach (var row in live) { working += row.WorkingBytes; privateBytes += row.PrivateBytes; }
        double disk = 0; int diskKnown = 0;
        foreach (var row in live) { DiskText(row, out double value); if (value >= 0) { disk += value; ++diskKnown; } }
        var traffic = live.Aggregate(default(ProcessBranchMetric), (sum, row) => sum.Add(row.Network));
        string partial(int known) => known != group.Members.Length ? "≥ " : "";
        string name = (group.IconRow is { } root ? ApplicationNames.Get(root, DispatcherQueue, RefreshAppTable) : null) ?? group.IconRow?.Name ?? group.Title;
        bool expanded = expandedApps.Contains(group.Key);
        string windows = string.Join("\n", group.Windows.Where(window => window.Title.Length > 0).Take(8).Select(window => "• " + GlobalObjectSearchText.Flat(window.Title, 120)));
        line.Set(expanded ? "⌄" : "›", $"{name} ({group.Members.Length})", "",
            cpu.Length == 0 ? "—" : partial(cpu.Length) + Display.Number(cpuValue, " %"),
            live.Length == 0 ? "—" : partial(live.Length) + Display.Bytes(working), MemoryTip(Loc.T("Pracovní sada všech procesů"), working, privateBytes),
            diskKnown == 0 ? "—" : partial(diskKnown) + Display.Number(disk / 1048576, " MiB/s"),
            traffic.NetworkText(live.Length, live.Length != group.Members.Length),
            name + " · " + group.Summary + (windows.Length > 0 ? "\n" + Loc.T("Okna:") + "\n" + windows : ""),
            $"{name}, {Plural(group.Members.Length, Loc.T("proces"), Loc.T("procesy"), Loc.T("procesů"))}, {(expanded ? Loc.T("rozbaleno") : Loc.T("sbaleno"))}");
        line.SetSort(name, cpu.Length == 0 ? -1 : cpuValue, working, diskKnown == 0 ? -1 : disk, traffic.SortValue);
        return line;
    }

    private GroupLine AppProcessLine(ProcessKey group, ProcessRow row)
    {
        if (!appProcessItems.TryGetValue(row.Key, out var line) || !ReferenceEquals(line.Process, row) || !Equals(line.Group, group)) {
            appProcessItems[row.Key] = line = new GroupLine(group, row, row);
        }
        string disk = DiskText(row, out double diskValue);
        line.Set("", row.Name, "", row.CpuText, Display.Bytes(row.WorkingBytes), MemoryTip(Loc.T("Pracovní sada"), row.WorkingBytes, row.PrivateBytes),
            disk, row.NetworkText, $"{row.Name} · PID {row.PidText}", $"{row.Name}, PID {row.PidText}");
        line.SetSort(row.Name, row.Cpu.State == SampleState.Valid ? row.Cpu.Value : -1, row.WorkingBytes, diskValue, row.Network.SortValue);
        return line;
    }
}
