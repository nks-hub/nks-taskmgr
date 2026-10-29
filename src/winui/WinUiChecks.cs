using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static Action BeginMinimizedCollection(MonitorModel model)
    {
        var own = model.Processes.Single(row => row.Key.Pid == Environment.ProcessId);
        var cpu = model.Resources.Single(row => row.Kind == ResourceKind.Cpu);
        long before = model.ProcessTime;
        int history = cpu.History.Count, notifications = 0;
        System.ComponentModel.PropertyChangedEventHandler changed = (_, _) => ++notifications;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler reordered = (_, _) => ++notifications;
        own.PropertyChanged += changed; cpu.PropertyChanged += changed; model.VisibleProcesses.CollectionChanged += reordered; model.Resources.CollectionChanged += reordered;
        return () => {
            own.PropertyChanged -= changed; cpu.PropertyChanged -= changed; model.VisibleProcesses.CollectionChanged -= reordered; model.Resources.CollectionChanged -= reordered;
            Require(model.ProcessTime > before && cpu.History.Count > history, "Minimized collection or history stopped.");
            Require(notifications == 0, "Minimized model still updated the UI.");
        };
    }

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

    public static unsafe void Services(ServiceModel services)
    {
        var rpc = services.Find("rpcss") ?? throw new InvalidOperationException("RPC service was not enumerated.");
        services.SetFilter(ServiceFilter.All); services.SetQuery("RPCSS"); services.Select(rpc.Name);
        Require(services.Visible.Contains(rpc) && services.Selected == rpc, "Service search or selection lost its identity.");
        services.SetFilter(rpc.State == 4 ? ServiceFilter.Stopped : ServiceFilter.Running);
        Require(services.Selected is null, "Hidden service became a different selected row.");
        services.SetFilter(ServiceFilter.All); services.SetQuery("");
        Require(services.Selected == rpc, "Clearing the service filter lost the selection anchor.");
        services.Sort(3); Require(services.Selected == rpc, "Service sorting changed the selection.");
        var row = new ServiceRow("DemoSvc");
        fixed (char* title = "Síťová služba") {
            row.Update(new ServiceInfo { Title = new String16 { Data = title, Length = 13 }, State = 7, Pid = 314,
                Config = new ServiceConfigInfo { StartType = 3 } });
        }
        Require(!row.Matches(ServiceFilter.Stopped, "") && row.Matches(ServiceFilter.Other, "SÍŤOVÁ"), "Paused service or Unicode search is incorrect.");
        row.Update(new ServiceInfo { State = 4, Pid = 314, Config = new ServiceConfigInfo { Error = 5 } });
        Require(row.Startup == "Chybí oprávnění (5)", "Service access denial became a startup value.");
        var host = new ProcessRow(new ProcessKey(1, 314, GenerationKind.CreationTime, 100));
        var detail = new ServiceDetailData(NativeMonitor.MonotonicNowNs, "demosvc", "", ServiceConfiguration.Empty, 0, 0, 0, 0, 314, 4, 100, []);
        Require(detail.MatchesHost(row, host), "Service host lost its case-insensitive identity.");
        Require(!detail.MatchesHost(row, new ProcessRow(new ProcessKey(1, 314, GenerationKind.CreationTime, 101))), "A recycled host PID opened a different process.");
        Require(!(detail with { HostError = 5 }).MatchesHost(row, host), "An unverified service host became selectable.");
        host.SetStatus(true, false); Require(!detail.MatchesHost(row, host), "An ended service host remained selectable.");
    }
}
