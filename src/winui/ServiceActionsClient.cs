using System.ComponentModel;

namespace NksTaskMgr.WinUI;

internal sealed class ServiceActionsClient : IDisposable
{
    private static int pending;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private ServiceActionsSession? active;
    private bool disposed;
    internal bool IsBusy => Volatile.Read(ref pending) != 0;

    internal async Task<ServiceActionsBatch> ExecuteAsync(ServiceAction action, string name,
        Func<ServiceActionPlan, CancellationToken, Task<uint[]?>> confirmation, IProgress<ServiceActionItem[]>? progress, CancellationToken cancellation)
    {
        lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) throw new IOException("Jiná akce služeb nebo její ukončování ještě probíhá.");
        var session = new ServiceActionsSession(); ServiceActionPlan? plan = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        using var stop = linked.Token.Register(session.Cancel);
        lock (sync) active = session;
        try {
            plan = await TimedAsync(token => Task.Run(() => session.PlanAsync(action, name, token)), TimeSpan.FromSeconds(5), session, linked.Token);
            uint[]? approved;
            {
                using var confirmTime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var confirmStop = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, confirmTime.Token);
                using var abort = confirmStop.Token.Register(session.Cancel);
                approved = await confirmation(plan, confirmStop.Token);
                if (approved is null || confirmStop.IsCancellationRequested) return Interrupted(plan, session, ServiceActionStatus.Cancelled, "Plán byl zrušen. Změna nebyla odeslána.");
            }
            var result = await TimedAsync(token => Task.Run(() => session.ApplyAsync(plan, approved, progress, token)), TimeSpan.FromSeconds(30), session, linked.Token);
            int confirmed = result.Items.Count(item => item.Outcome is ServiceActionOutcome.Confirmed or ServiceActionOutcome.AlreadyDesired);
            CapabilityReports.ObserveServiceAction((result.Status == ServiceActionStatus.Ok ? "Potvrzený stav" : "Částečný nebo odmítnutý výsledek")
                + ": " + confirmed + "/" + result.Items.Length + ", " + ServiceActionsProtocol.StatusText(result.Status) + ".");
            return result;
        } catch (ServiceActionException error) { return Interrupted(plan, session, error.Status, error.Message, error.Error); }
        catch (TimeoutException error) { return Interrupted(plan, session, ServiceActionStatus.Timeout, error.Message, 1460); }
        catch (OperationCanceledException) { return Interrupted(plan, session, ServiceActionStatus.Cancelled,
            session.ApplySent ? "Čekání bylo zrušeno. SCM mohl požadavek přijmout; výsledek není známý." : "Plán byl zrušen. Změna nebyla odeslána.", 1223); }
        catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception or ObjectDisposedException) {
            return Interrupted(plan, session, ServiceActionStatus.Error, error.Message);
        } finally {
            session.Cancel(); _ = Task.Run(() => RetireAsync(session));
        }
    }
    private static async Task<T> TimedAsync<T>(Func<CancellationToken, Task<T>> action, TimeSpan timeout, ServiceActionsSession session, CancellationToken cancellation)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        using var abort = linked.Token.Register(session.Cancel);
        var work = action(linked.Token);
        _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await work.WaitAsync(linked.Token); }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested) {
            throw new TimeoutException("Vypršel čas čekání na pracovníka. Rozpracovaný stav nemusí být konečný.", error);
        }
    }
    private static ServiceActionsBatch Interrupted(ServiceActionPlan? plan, ServiceActionsSession session, ServiceActionStatus status, string message, uint error = 0)
    {
        var last = session.Latest;
        var rows = plan?.Nodes.Select(node => {
            var known = last.FirstOrDefault(item => item.Id == node.Id);
            return known is null ? new ServiceActionItem(node.Id, session.ApplySent ? ServiceActionOutcome.Unknown : ServiceActionOutcome.Cancelled,
                0, 0, session.ApplySent ? ServiceResultFlags.MayHaveApplied : 0, 0, 0, 0, 0, 0, 0, 0)
                : known with { Outcome = ServiceActionOutcome.Unknown, Flags = known.Flags | ServiceResultFlags.MayHaveApplied };
        }).ToArray() ?? [];
        return new(plan, status, error, rows, false, session.ApplySent, message);
    }
    private async Task RetireAsync(ServiceActionsSession session)
    {
        try {
            await session.RetireAsync().ConfigureAwait(false);
            lock (sync) if (ReferenceEquals(active, session)) active = null;
            Interlocked.Exchange(ref pending, 0);
        } catch (Exception error) when (error is Win32Exception or InvalidOperationException or ObjectDisposedException) {
            // Keep the lease when actual termination could not be established.
        }
    }
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; lifetime.Cancel(); active?.Cancel(); } }
}
