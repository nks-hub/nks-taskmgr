namespace NksTaskMgr.WinUI;

internal static class DumpRecoveryIo
{
    private static readonly SemaphoreSlim worker = new(1);
    private static int pending;
    internal static async Task<IDisposable> EnterAsync(CancellationToken cancellation)
    {
        if (Interlocked.Increment(ref pending) > 4) { Interlocked.Decrement(ref pending); throw new IOException("Fronta ukládání a obnovy dumpů je obsazená."); }
        try { await worker.WaitAsync(cancellation).ConfigureAwait(false); return new Lease(); }
        catch { Interlocked.Decrement(ref pending); throw; }
    }
    private sealed class Lease : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) != 0) return; worker.Release(); Interlocked.Decrement(ref pending); }
    }
}
