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
        ServiceChecks.Verify();
        Require(!RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled, "The probe must run as a published Native AOT executable.");
        Require(sizeof(FrameView) == 288 && sizeof(ProcessInfo) == 152 && sizeof(ResourceInfo) == 128, "Managed ABI layout mismatch.");
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
        uint processCount = 0, logicalCount = 0, disks = 0, gpus = 0, networks = 0;
        bool resourceStream = false;
        var watch = Stopwatch.StartNew();
        try {
            while (watch.Elapsed < TimeSpan.FromSeconds(10)) {
                using var frame = monitor.Read();
                frame?.Read(view => {
                    Require(view.AbiVersion == 1 && view.ProcessError.Length == 0 && view.ResourceError.Length == 0, "The native stream reported an error.");
                    if ((view.Flags & FrameFlags.ProcessData) != 0 && view.Processes.CpuPercent.State == SampleState.Valid) {
                        processCount = view.Processes.ProcessCount;
                        logicalCount = view.Processes.LogicalProcessors;
                        Require(view.Processes.ProcessorCount == logicalCount && view.Processes.Memory.State == SampleState.Valid
                            && view.Processes.Memory.AvailableBytes <= view.Processes.Memory.PhysicalBytes, "CPU or memory fields were misaligned.");
                        Require(view.Processes.MeasuredAtNs <= view.ReadAtNs && view.ReadAtNs <= NativeMonitor.MonotonicNowNs, "The managed client mixed monotonic clocks.");
                        for (uint i = 0; i < processCount; ++i) {
                            var process = view.Processes.Processes[i];
                            if (process.Pid != (uint)Environment.ProcessId) continue;
                            Require(process.GenerationKind == GenerationKind.CreationTime && process.Generation != 0, "The native process identity was lost.");
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
                if (retained is not null && resourceStream) break;
                Thread.Sleep(20);
            }
            Require(retained is not null && ownName.Length > 0 && resourceStream, "Both real native streams did not arrive.");
            monitor.Dispose();
            GC.Collect(); GC.WaitForPendingFinalizers();
            bool survived = false;
            retained!.Read(view => {
                for (uint i = 0; i < view.Processes.ProcessCount; ++i) {
                    var process = view.Processes.Processes[i];
                    if (process.Pid == (uint)Environment.ProcessId) survived = process.Generation == generation && process.Name.Copy() == ownName;
                }
            });
            Require(survived, "A retained AOT frame depended on the monitor or garbage collector.");
            Console.WriteLine($"NativeAOT=True; processes={processCount}; logical={logicalCount}; disks={disks}; gpus={gpus}; networks={networks}; retained=True");
        } finally { retained?.Dispose(); }
    }

    public static int Main()
    {
        try { Verify(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
