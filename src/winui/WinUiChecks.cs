using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static unsafe void ProcessNotifications()
    {
        var row = new ProcessRow(new ProcessKey(new(1, 2), 42, GenerationKind.CreationTime, 1));
        fixed (char* name = "Example") {
            var source = new ProcessInfo { Name = new String16 { Data = name, Length = 7 }, CpuPercent = new Metric { State = SampleState.First, Source = MetricSource.NativeProcess } };
            row.Update(source);
            var changed = new List<string?>();
            row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
            row.Update(source);
            Require(changed.Count == 0, "An unchanged process snapshot invalidated its bindings.");
            source.CpuPercent = new Metric { State = SampleState.Valid, Source = MetricSource.NativeProcess };
            row.Update(source);
            Require(row.CpuText == "0,0 %" && changed.Contains(nameof(ProcessRow.CpuText)) && !changed.Contains(nameof(ProcessRow.WorkingText)), "First-to-zero CPU did not update only the affected cells.");
            changed.Clear(); source.WorkingSetBytes = 64 * 1024 * 1024; row.Update(source);
            Require(row.WorkingText == "64 MiB" && changed.Contains(nameof(ProcessRow.WorkingText)) && !changed.Contains(nameof(ProcessRow.CpuText)), "A memory update invalidated unrelated cells.");
            row.SetStatus(false, true); changed.Clear(); row.Update(source);
            Require(row.CpuText == "0,0 %" && changed.Any(string.IsNullOrEmpty), "Returning from stale data left stale cell text.");
            row.SetStatus(true, false); row.Update(source);
            Require(!row.Ended && !row.DisplayName.Contains("ukončeno", StringComparison.Ordinal), "A fresh snapshot retained ended process text.");
        }
    }

    internal static void IconWorkerIsolation(ProcessKey own)
    {
        using var worker = new IconWorker();
        worker.Start();
        using var stalled = System.Diagnostics.Process.GetProcessById(worker.ProcessId);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool timedOut = false;
        try { worker.ReadResponse(TimeSpan.FromMilliseconds(100)); } catch (TimeoutException) { timedOut = true; }
        Require(timedOut && watch.Elapsed < TimeSpan.FromSeconds(2), "A silent icon worker did not time out.");
        Require(stalled.WaitForExit(2000), "The timed-out icon worker remained alive.");
        var bytes = worker.Request(own);
        Require(bytes is { Length: 1350 } && bytes[0] == (byte)'B' && bytes[1] == (byte)'M', "The icon worker did not recover after a timeout. " + worker.Failure);
        Require(worker.ProcessId != stalled.Id, "A timed-out icon worker was reused.");
        Require(worker.Request(own with { Generation = own.Generation + 1 }) is null, "The icon worker accepted a reused PID.");
        using var running = System.Diagnostics.Process.GetProcessById(worker.ProcessId);
        watch.Restart(); worker.Stop();
        Require(watch.Elapsed < TimeSpan.FromMilliseconds(200), "Stopping the icon worker blocked its owner.");
        Require(running.WaitForExit(2000), "The stopped icon worker remained alive.");
        Require(worker.Request(own) is null, "A stopped icon worker restarted during shutdown.");
    }

    internal static async Task SharedIconDecode(byte[] bytes)
    {
        var first = ProcessIconView.DecodeShared(bytes);
        Require(ReferenceEquals(first, ProcessIconView.DecodeShared(bytes)), "Identical executable bytes started separate decodes.");
        Require((await first).PixelWidth > 0, "The shared executable icon did not decode.");
        var damaged = new byte[bytes.Length];
        var failed = ProcessIconView.DecodeShared(damaged);
        bool rejected = false;
        try { await failed; } catch (System.Runtime.InteropServices.COMException) { rejected = true; }
        Require(rejected, "Invalid bitmap bytes were accepted.");
        bytes.CopyTo(damaged, 0);
        var retried = ProcessIconView.DecodeShared(damaged);
        Require(!ReferenceEquals(failed, retried) && (await retried).PixelWidth > 0, "A failed decode permanently poisoned the icon cache.");
        Require(ReferenceEquals(retried, ProcessIconView.DecodeShared(damaged)), "A successful retry was not shared.");
    }

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
        Require(model.BootIdentity.State == SampleState.Valid && !model.BootIdentity.Identifier.IsEmpty
            && own.Key.Boot == model.BootIdentity.Identifier, "WinUI lost the full boot GUID.");
        var distinctBoot = own.Key with { Boot = own.Key.Boot with { High = own.Key.Boot.High ^ 1 } };
        Require(distinctBoot != own.Key, "Process identity ignored the upper half of the boot GUID.");
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
        Graphs();
    }

    private static void DeepTree()
    {
        var rows = new List<ProcessRow>();
        for (uint i = 1; i <= 10_000; ++i) {
            var row = new ProcessRow(new ProcessKey(new(1, 2), i, GenerationKind.CreationTime, i));
            row.Update(new ProcessInfo { ParentPid = i - 1 }); rows.Add(row);
        }
        var expanded = ProcessTree.Order(rows, new HashSet<ProcessKey>());
        Require(expanded.Count == 10_000 && expanded[^1].Depth == 12, "Deep tree failed or exceeded the indentation limit.");
        Require(ProcessTree.Order(rows, new HashSet<ProcessKey> { rows[0].Key }).Count == 1, "Collapsed deep tree leaked descendants.");
        var a = new ProcessRow(new ProcessKey(new(1, 2), 1, GenerationKind.CreationTime, 10));
        var b = new ProcessRow(new ProcessKey(new(1, 2), 2, GenerationKind.CreationTime, 10));
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
        var host = new ProcessRow(new ProcessKey(new(1, 2), 314, GenerationKind.CreationTime, 100));
        var detail = new ServiceDetailData(NativeMonitor.MonotonicNowNs, "demosvc", "", ServiceConfiguration.Empty, 0, 0, 0, 0, 314, 4, 100, []);
        Require(detail.MatchesHost(row, host), "Service host lost its case-insensitive identity.");
        Require(!detail.MatchesHost(row, new ProcessRow(new ProcessKey(new(1, 2), 314, GenerationKind.CreationTime, 101))), "A recycled host PID opened a different process.");
        Require(!(detail with { HostError = 5 }).MatchesHost(row, host), "An unverified service host became selectable.");
        host.SetStatus(true, false); Require(!detail.MatchesHost(row, host), "An ended service host remained selectable.");
    }
}
