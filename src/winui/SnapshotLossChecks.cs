using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private Task? smokeSnapshotLoss;

    /// <summary>Blocks the UI thread long enough for the native snapshot slot to be overwritten, then requires the process
    /// history to report one gap whose missing count matches the stall instead of counting the loss twice.</summary>
    private async Task CheckProcessSnapshotLossAsync()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        await NextRenderedFrame();
        long before = model.ProcessTime;
        Require(before > 0, "No process snapshot before the stall check.");
        const int stallMs = 3500;
        Thread.Sleep(stallMs);
        for (int i = 0; i < 50 && model.ProcessTime <= before; ++i) await Task.Delay(100);
        long first = model.ProcessTime;
        for (int i = 0; i < 50 && model.ProcessTime <= first; ++i) await Task.Delay(100);
        var selection = model.SelectProcessHistory(before, NativeMonitor.MonotonicNowNs);
        double expected = stallMs / (double)model.Interval;
        Require(selection.GapCount >= 1 && selection.Gaps.Any(gap => gap.MissingDeliveryRevisions > 0),
            $"A {stallMs} ms UI stall left no process history gap (gaps={selection.GapCount}, missing={selection.KnownMissingRevisions}).");
        Require(selection.KnownMissingRevisions >= 1 && selection.KnownMissingRevisions <= expected + 1,
            $"A {stallMs} ms stall at {model.Interval} ms reported {selection.KnownMissingRevisions} missing snapshots instead of about {expected:F0}.");
        App.Trace($"Process snapshot loss passed: {stallMs} ms UI stall at {model.Interval} ms gave {selection.GapCount} gap(s), {selection.KnownMissingRevisions} missing snapshot(s), {selection.FrameCount} retained frames");
    }
}
