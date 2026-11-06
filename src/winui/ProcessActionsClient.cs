namespace NksTaskMgr.WinUI;

internal sealed class ProcessActionsClient : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private ProcessActionsSession? active;
    private int pending;
    private bool disposed;
    private readonly bool elevated;
    internal bool IsBusy => Volatile.Read(ref pending) != 0;
    internal ProcessActionsClient(bool elevated = false) => this.elevated = elevated;

    internal async Task<ProcessActionsBatch> ExecuteAsync(ProcessActionsRequest request, CancellationToken cancellation)
    {
        lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.CompareExchange(ref pending, 1, 0) != 0)
            throw new IOException(Loc.T("Jiný dotaz nebo akce ještě probíhá. Počkejte na výsledek."));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(elevated ? 175 : 5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token, deadline.Token);
        var session = new ProcessActionsSession(elevated);
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
            if (!elevated && request.Command != ProcessActionCommand.TerminateTree) CapabilityReports.ObserveActions(result);
            return result;
        } catch (ElevationBrokerTrustException) { throw;
        } catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested && !lifetime.IsCancellationRequested) {
            if (request.Command == ProcessActionCommand.TerminateTree)
                throw new ProcessTreeActionsUnknownException(Loc.T("Vypršel limit. Výsledek rozpracovaného ukončování stromu není známý."), session.TreePlan, error);
            throw new TimeoutException(elevated ? Loc.T("Čekání na zvýšení práv nebo výsledek skončilo. Výsledek případné akce není známý.")
                : Loc.T("Pracovník neodpověděl do 5 sekund. Výsledek rozpracované akce není známý."), error);
        } catch (OperationCanceledException error) when (request.Command == ProcessActionCommand.TerminateTree) {
            throw new ProcessTreeActionsUnknownException(Loc.T("Čekání bylo zrušeno. Akce na známých i nově zjištěných cílech se mohly provést."), session.TreePlan, error);
        } catch (Exception error) when (request.Command == ProcessActionCommand.TerminateTree && error is IOException or InvalidDataException) {
            throw new ProcessTreeActionsUnknownException(Loc.F("Chybí úplný ověřený výsledek stromu. Akce se mohly provést. {0}", error.Message), session.TreePlan, error);
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
