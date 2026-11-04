using System.Diagnostics;
using System.Runtime.CompilerServices;
using NksTaskMgr.Native;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static unsafe void Verify()
    {
        var registration = NativeReplacement.Read(@"C:\Nks Probe\NksTaskMgr.WinUI.exe");
        Require(registration.Error == 0 && registration.Status != ReplacementStatus.ThisInstall && (registration.Status != ReplacementStatus.NotRegistered || registration.Debugger.Length == 0),
            "The managed replacement state does not match the native HKLM read.");
        ServiceChecks.Verify();
        Require(sizeof(StartupItemInfo) == 80 && sizeof(StartupView) == 16, "Managed startup layout mismatch.");
        var startup = NativeStartup.Read();
        Require(startup.All(item => item.Location.Length > 0 && Enum.IsDefined(item.Source) && Enum.IsDefined(item.State)), "A startup item lost its location or state.");
        bool machineRefused = false;
        try { NativeStartup.SetEnabled(StartupSource.MachineRun, "NksProbeMissing", false); }
        catch (NativeMonitorException error) when (error.Result == NativeResult.InvalidArgument) { machineRefused = true; }
        Require(machineRefused, "A machine-wide startup item crossed the unelevated write boundary.");
        Require(NativeStartup.SetEnabled(StartupSource.UserRun, "NksProbeMissing-" + Guid.NewGuid().ToString("N"), false) == 2, "A missing Run value was approved.");
        Require(!RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled, "The probe must run as a published Native AOT executable.");
        Require(sizeof(FrameView) == 288 && sizeof(ProcessInfo) == 152 && sizeof(ResourceInfo) == 128, "Managed ABI layout mismatch.");
        Require(sizeof(GpuEngineInfo) == 48 && sizeof(GpuInfo) == 56 && sizeof(GpuData) == 16, "Managed GPU layout mismatch.");
        Require(sizeof(BootIdentityInfo) == 24, "Managed boot identity layout mismatch.");
        Require(sizeof(NetworkTransferInfo) == 56 && sizeof(NetworkTransferData) == 16, "Managed network transfer layout mismatch.");
        Require(sizeof(SessionSum) == 16 && sizeof(SessionInfo) == 136 && sizeof(SessionData) == 32, "Managed session layout mismatch.");
        using var monitor = NativeMonitor.Open(250, paused: true);
        Thread.Sleep(300);
        using var pausedFrame = monitor.Read();
        Require(pausedFrame is null, "The paused monitor published a frame.");
        bool rejected = false;
        try { monitor.SetInterval(0); } catch (NativeMonitorException error) when (error.Result == NativeResult.InvalidArgument) { rejected = true; }
        Require(rejected, "An invalid interval crossed the native boundary.");
        monitor.SetPaused(false);
        NativeFrame? retained = null;
        string ownName = string.Empty;
        ulong generation = 0;
        BootIdentifier boot = default;
        uint processCount = 0, logicalCount = 0, disks = 0, gpus = 0, networks = 0;
        bool resourceStream = false;
        var watch = Stopwatch.StartNew();
        try {
            while (watch.Elapsed < TimeSpan.FromSeconds(10)) {
                using var frame = monitor.Read();
                frame?.Read(view => {
                    Require(view.AbiVersion == 1 && view.ProcessError.Length == 0 && view.ResourceError.Length == 0, "The native stream reported an error.");
                    if ((view.Flags & FrameFlags.ProcessData) != 0 && view.Processes.CpuPercent.State == SampleState.Valid) {
                        var identity = frame!.ReadBootIdentity();
                        Require(identity is { State: SampleState.Valid } && !identity.Value.Identifier.IsEmpty, "The AOT frame lost the boot GUID.");
                        if (!boot.IsEmpty) Require(boot == identity!.Value.Identifier, "The boot GUID changed during sampling.");
                        boot = identity!.Value.Identifier;
                        processCount = view.Processes.ProcessCount;
                        logicalCount = view.Processes.LogicalProcessors;
                        Require(view.Processes.ProcessorCount == logicalCount && view.Processes.Memory.State == SampleState.Valid
                            && view.Processes.Memory.AvailableBytes <= view.Processes.Memory.PhysicalBytes, "CPU or memory fields were misaligned.");
                        Require(view.Processes.MeasuredAtNs <= view.ReadAtNs && view.ReadAtNs <= NativeMonitor.MonotonicNowNs, "The managed client mixed monotonic clocks.");
                        for (uint i = 0; i < processCount; ++i) {
                            var process = view.Processes.Processes[i];
                            if (process.Pid != (uint)Environment.ProcessId) continue;
                            // Builds with SystemBasicProcessInformation publish sequence numbers instead of creation times.
                            Require(process.GenerationKind is GenerationKind.CreationTime or GenerationKind.SequenceNumber && process.Generation != 0, "The native process identity was lost.");
                            ownName = process.Name.Copy(); generation = process.Generation;
                            retained ??= frame!.Clone();
                        }
                    }
                    if ((view.Flags & FrameFlags.ResourceData) != 0) {
                        resourceStream = true; disks = gpus = networks = 0;
                        for (uint i = 0; i < view.Resources.ResourceCount; ++i) {
                            var resource = view.Resources.Resources[i];
                            Require(resource.Id.Length != 0 && resource.Id.Copy().Length != 0, "The resource identity was lost.");
                            switch (resource.Kind) { case ResourceKind.Disk: ++disks; break; case ResourceKind.Gpu: ++gpus; break; case ResourceKind.Network: ++networks; break; }
                        }
                    }
                });
                frame?.ReadGpu(data => {
                    for (uint i = 0; i < data.GpuCount; ++i) {
                        var gpu = data.Gpus[i]; Require(gpu.ResourceId.Copy().Length > 0 && gpu.SharedCapacity >= 0, "AOT GPU identity or capacity was lost.");
                        for (uint j = 0; j < gpu.EngineCount; ++j) Require(gpu.Engines[j].Id.Copy().Length > 0 && gpu.Engines[j].Name.Copy().Length > 0, "AOT GPU engine strings were lost.");
                    }
                });
                frame?.ReadNetworkTransfers(data => {
                    Require(data.AdapterCount == networks, "AOT network transfer entries differ from network resources.");
                    for (uint i = 0; i < data.AdapterCount; ++i)
                        Require(data.Adapters[i].ResourceId.Copy().Length > 0 && data.Adapters[i].StartedAtNs > 0, "AOT network transfer identity or start was lost.");
                });
                if (retained is not null && resourceStream) break;
                Thread.Sleep(20);
            }
            Require(retained is not null && ownName.Length > 0 && resourceStream, "Both real native streams did not arrive.");
            monitor.Dispose();
            GC.Collect(); GC.WaitForPendingFinalizers();
            Require(retained!.ReadBootIdentity()?.Identifier == boot, "A retained AOT frame lost the boot GUID.");
            bool survived = false;
            retained!.Read(view => {
                for (uint i = 0; i < view.Processes.ProcessCount; ++i) {
                    var process = view.Processes.Processes[i];
                    if (process.Pid == (uint)Environment.ProcessId) survived = process.Generation == generation && process.Name.Copy() == ownName;
                }
            });
            Require(survived, "A retained AOT frame depended on the monitor or garbage collector.");
            uint sessions = 0;
            retained.ReadSessions(data => {
                Require(data.State == SampleState.Valid && data.ProcessCount == processCount, "AOT session data was incomplete.");
                for (uint i = 0; i < data.SessionCount; ++i)
                    if (data.Sessions[i].SessionId == (uint)Process.GetCurrentProcess().SessionId)
                        Require(data.Sessions[i].User.Copy().Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase), "AOT session user was lost.");
                sessions = data.SessionCount;
            });
            Require(sessions > 0, "AOT session data was missing.");
            Console.WriteLine($"NativeAOT=True; processes={processCount}; logical={logicalCount}; disks={disks}; gpus={gpus}; networks={networks}; sessions={sessions}; retained=True");
        } finally { retained?.Dispose(); }
    }

    public static int Main()
    {
        try { Verify(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
