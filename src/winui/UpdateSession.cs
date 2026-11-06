using System.ComponentModel;

namespace NksTaskMgr.WinUI;

internal sealed class UpdateSession : IDisposable
{
    private readonly object sync = new();
    private readonly UpdateWorkerProcess worker;
    private readonly byte[] readyRequest;
    private readonly Action release;
    private readonly CancellationTokenRegistration cancelClient, cancelCaller;
    private int commitSent, accepted, attempt;
    private bool disposed, retiring;
    internal UpdatePlan Plan { get; }
    internal ulong CommitDeadlineMonotonicMs { get; }
    internal bool CommitSent => Volatile.Read(ref commitSent) != 0;
    internal bool HandoffAccepted => Volatile.Read(ref accepted) != 0;

    internal UpdateSession(UpdatePlan plan, UpdateWorkerProcess worker, byte[] readyRequest, ulong deadline, Action release,
        CancellationToken lifetime, CancellationToken caller)
    {
        Plan = plan; this.worker = worker; this.readyRequest = readyRequest; CommitDeadlineMonotonicMs = deadline; this.release = release;
        cancelClient = lifetime.Register(Dispose); cancelCaller = caller.Register(Dispose);
    }
    internal async Task<UpdateHandoff> CommitAsync(CancellationToken cancellation)
    {
        if (Interlocked.CompareExchange(ref attempt, 1, 0) != 0) throw new IOException(Loc.T("Předání aktualizace se nesmí opakovat."));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        var request = UpdateProtocol.CommitRequest(readyRequest);
        try {
            lock (sync) {
                ObjectDisposedException.ThrowIf(disposed, this); cancellation.ThrowIfCancellationRequested();
                if (UpdateProtocol.NowMs >= CommitDeadlineMonotonicMs) throw new UpdateException(UpdateStatus.Rejected, Loc.T("Platnost potvrzení aktualizace vypršela."), 1460);
                Interlocked.Exchange(ref commitSent, 1);
            }
            await worker.WriteAsync(request, true, linked.Token).ConfigureAwait(false);
            var result = await worker.ReadAsync(request, linked.Token).ConfigureAwait(false);
            if (result.Frame != UpdateFrame.Accepted || result.Status != UpdateStatus.Ok
                || result.JournalState is not (UpdateJournalState.Committed or UpdateJournalState.WaitingGui))
                throw new UpdateException(result.Status, result.Detail.Length > 0 ? result.Detail : Loc.T("Předání instalace nebylo potvrzené."), result.Error);
            Interlocked.Exchange(ref accepted, 1);
            return new(true, true, result.Status, result.Error, result.JournalState,
                Loc.T("Pracovník převzal plán. Instalace začne až po skutečném ukončení tohoto procesu; dokončení ověřte v příštím spuštění."));
        } catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception or OperationCanceledException or ObjectDisposedException) {
            return new(false, CommitSent, error is UpdateException update ? update.Status : UpdateStatus.Unknown,
                error is UpdateException known ? known.Error : 0, CommitSent ? UpdateJournalState.Unknown : UpdateJournalState.Rejected,
                CommitSent ? Loc.F("Potvrzení pracovníka nedorazilo. Plán mohl být předán; neopakujte požadavek a zkontrolujte deník. {0}", error.Message) : error.Message);
        } finally { Dispose(); }
    }
    public void Dispose()
    {
        lock (sync) {
            if (disposed) return; disposed = true;
            if (retiring) return; retiring = true;
        }
        _ = Task.Run(RetireAsync);
    }
    private async Task RetireAsync()
    {
        try {
            // After any Commit bytes may have been sent, killing the peer could corrupt its journal or installer.
            await worker.RetireAsync(!CommitSent).ConfigureAwait(false);
            cancelClient.Dispose(); cancelCaller.Dispose(); release();
        } catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException) {
            // The global lease stays held if the peer's actual exit is not established.
        }
    }
}
