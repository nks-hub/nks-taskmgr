using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private async Task CheckApplicationInventoryRecoveryAsync()
    {
        await WaitForInventoryAsync(() => model.ProcessTime > 0 && model.ResourceTime > 0 && !model.Stale
            && model.Applications.HasInventory && applicationRequest is null, "Initial live inventory");
        model.PausePublisherForInventoryCheck(true);
        long processTime, resourceTime;
        try {
            // A collector request already in flight has a 5 s deadline.
            long drainAfter = NativeMonitor.MonotonicNowNs + 6_000_000_000;
            await WaitForInventoryAsync(() => NativeMonitor.MonotonicNowNs >= drainAfter && model.Stale
                && NativeMonitor.MonotonicNowNs - model.ResourceTime > MonitorModel.StaleAfterNs, "Publisher drain");
            processTime = model.ProcessTime;
            resourceTime = model.ResourceTime;
            long started = NativeMonitor.MonotonicNowNs;
            if (model.Paused || !model.Stale) throw new InvalidOperationException("Missing frames did not mark the running model stale.");
            await Task.Delay(12500);
            App.Trace($"application-inventory processTime={processTime};currentProcessTime={model.ProcessTime};resourceUnchanged={resourceTime == model.ResourceTime};receivedAfterStartNs={model.Applications.ReceivedAt - started};stale={model.Applications.Stale};error={model.Applications.Error}");
            if (model.ProcessTime != processTime || model.ResourceTime != resourceTime || !model.Stale)
                throw new InvalidOperationException("The missing-frame scenario received new metrics or lost the stale state.");
            if (model.Applications.ReceivedAt <= started + 8_000_000_000 || model.Applications.Stale)
                throw new InvalidOperationException("Application inventory stopped refreshing while metric frames were missing.");
        } finally { model.PausePublisherForInventoryCheck(false); }
        await WaitForInventoryAsync(() => model.ProcessTime > processTime && model.ResourceTime > resourceTime && !model.Stale, "Metrics recovery");
        App.Trace("application-inventory missingFrames=True;repeatedInventory=True;staleMetricsPreserved=True;recovered=True");
    }

    private static async Task WaitForInventoryAsync(Func<bool> ready, string stage)
    {
        long deadline = NativeMonitor.MonotonicNowNs + 10_000_000_000;
        while (!ready()) {
            if (NativeMonitor.MonotonicNowNs >= deadline) throw new TimeoutException(stage);
            await Task.Delay(100);
        }
    }
}

public sealed partial class MonitorModel
{
    // Pause the publisher without the UI pause flag to exercise real NKS_NO_DATA reads.
    internal void PausePublisherForInventoryCheck(bool value) => monitor.SetPaused(value);
}
