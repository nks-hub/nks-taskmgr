namespace NksTaskMgr.WinUI;

internal sealed class ProcessDumpClient : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private ProcessDumpSession? active;
    private string? cleanupError;
    private int pending;
    private bool disposed;
    internal bool IsBusy => Volatile.Read(ref pending) != 0;
    internal string? CleanupError => Volatile.Read(ref cleanupError);

    internal async Task<ProcessDumpResult> CaptureAsync(ProcessDumpTarget target, ProcessDumpMode mode, string path, CancellationToken cancellation)
    {
        lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) throw new IOException(Loc.T("Předchozí dump nebo jeho úklid ještě probíhá."));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token, deadline.Token);
        var session = new ProcessDumpSession(); bool started = false;
        try {
            lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); active = session; }
            Volatile.Write(ref cleanupError, null);
            var token = linked.Token;
            using var stop = token.Register(session.Cancel);
            var work = Task.Run(async () => {
                try { using var io = await DumpRecoveryIo.EnterAsync(token).ConfigureAwait(false); return await session.CaptureAsync(target, mode, path, token).ConfigureAwait(false); }
                finally {
                    Volatile.Write(ref cleanupError, session.CleanupError); session.Dispose();
                    lock (sync) if (ReferenceEquals(active, session)) active = null;
                    Interlocked.Exchange(ref pending, 0);
                }
            });
            started = true;
            _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var result = await work.WaitAsync(token).ConfigureAwait(false);
            string status = result.State == ProcessDumpState.Captured && result.SavedPath is not null ? Loc.T("Dump uložen") : Loc.F("Dump neuložen: {0}", result.State switch {
                ProcessDumpState.Denied => Loc.T("přístup odmítnut"), ProcessDumpState.Unsupported => Loc.T("nepodporováno"), ProcessDumpState.TargetChanged => Loc.T("proces skončil nebo se změnil"),
                ProcessDumpState.Unavailable => Loc.T("cíl nedostupný"), ProcessDumpState.LimitExceeded => Loc.T("překročen limit velikosti"), ProcessDumpState.Canceled => Loc.T("zrušeno nebo překročen čas"),
                ProcessDumpState.InvalidRequest => Loc.T("neplatný požadavek"), _ => Loc.T("výsledek nepotvrzen")
            }) + (result.Error == 0 ? "" : " (Windows/HRESULT 0x" + result.Error.ToString("X8") + ")");
            CapabilityReports.ObserveDump(target.Key, status);
            return result;
        } catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested && !lifetime.IsCancellationRequested) {
            throw new TimeoutException(Loc.T("Dump překročil limit 60 sekund. Výsledek není potvrzený; pracovník se ukončuje a vlastní dočasný soubor se uklízí."), error);
        } finally {
            if (linked.IsCancellationRequested) session.Cancel();
            if (!started) { session.Dispose(); Interlocked.Exchange(ref pending, 0); }
        }
    }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; lifetime.Cancel(); active?.Cancel(); }
    }
}
