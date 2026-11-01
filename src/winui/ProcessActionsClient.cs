namespace NksTaskMgr.WinUI;

internal sealed class ProcessActionsClient : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private ProcessActionsSession? active;
    private int pending;
    private bool disposed;
    internal bool IsBusy => Volatile.Read(ref pending) != 0;

    internal async Task<ProcessActionsBatch> ExecuteAsync(ProcessActionsRequest request, CancellationToken cancellation)
    {
        lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.CompareExchange(ref pending, 1, 0) != 0)
            throw new IOException("Jiný dotaz nebo akce ještě probíhá. Počkejte na výsledek.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token, deadline.Token);
        var session = new ProcessActionsSession();
        bool started = false;
        try {
            var bytes = ProcessActionsProtocol.Request(request);
            lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); active = session; }
            using var stop = linked.Token.Register(session.Cancel);
            var token = linked.Token;
            var work = Task.Run(async () => {
                try { return await session.ExecuteAsync(bytes, token).ConfigureAwait(false); }
                finally {
                    session.Dispose();
                    lock (sync) if (ReferenceEquals(active, session)) active = null;
                    Interlocked.Exchange(ref pending, 0);
                }
            });
            started = true;
            _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var result = await work.WaitAsync(linked.Token).ConfigureAwait(false);
            CapabilityReports.ObserveActions(result);
            return result;
        } catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested && !lifetime.IsCancellationRequested) {
            throw new TimeoutException("Pracovník neodpověděl do 5 sekund. Výsledek rozpracované akce není známý.", error);
        } finally {
            if (linked.IsCancellationRequested) session.Cancel();
            if (!started) { session.Dispose(); Interlocked.Exchange(ref pending, 0); }
        }
    }

    public void Dispose()
    {
        lock (sync) {
            if (disposed) return;
            disposed = true; lifetime.Cancel(); active?.Cancel();
        }
    }
}
