using System.ComponentModel;

namespace NksTaskMgr.WinUI;

internal enum UpdateCleanupCategory : uint { Package = 1, PlanGroup, Other }
internal enum UpdateCleanupOutcome : uint { Eligible, Kept, Deleted, Skipped, Failed, Unknown }
internal enum UpdateCleanupReason : uint {
    Unused, Current, Previous, LiveWorker, LiveInstaller, LifetimeUnknown, Unresolved, ResolutionProof, RootAnchor,
    UnknownRoot, UnknownData, MissingProof, StageInUse, Changed, Denied, Io, Dependency, Cancelled, Legacy, AlreadyAbsent
}
internal sealed record UpdateCleanupItem(string Id, UpdateCleanupCategory Category, UpdateCleanupOutcome Outcome, UpdateCleanupReason Reason,
    ulong? Bytes, uint Files, uint Directories, uint DeletedFiles, uint DeletedDirectories, ulong DeletedBytes,
    uint Error, int NtStatus, SignedImageLease.FileId ScopeRootFileId, string PlanId, string Name, string Detail, string Path);
internal sealed record UpdateCleanupTotals(ulong Files, ulong Directories, ulong KnownBytes, uint UnknownSizeCount, bool CountsKnown = true);
internal sealed record UpdateCleanupSummary(UpdateCleanupTotals Eligible, UpdateCleanupTotals Kept, UpdateCleanupTotals Deleted,
    UpdateCleanupTotals Skipped, UpdateCleanupTotals Failed, UpdateCleanupTotals Unknown, bool CompleteInventory);
internal sealed record UpdateCleanupPlan(string ManifestHash, ulong CommitDeadlineMonotonicMs, SignedImageLease.FileId CacheRootFileId,
    bool CompleteInventory, IReadOnlyList<UpdateCleanupItem> Items, UpdateCleanupSummary Summary);
internal sealed record UpdateCleanupResult(bool Completed, bool MayHaveDeleted, UpdateStatus Status, uint Error, string Detail,
    IReadOnlyList<UpdateCleanupItem> Items, UpdateCleanupSummary Summary);
internal sealed record UpdateCleanupResponse(UpdateFrame Frame, UpdateStatus Status, uint Error, bool CompleteInventory, bool Confirmed,
    bool MayHaveDeleted, string ManifestHash, ulong Deadline, SignedImageLease.FileId CacheRootFileId, IReadOnlyList<UpdateCleanupItem> Items);

internal sealed class UpdateCleanupSession : IDisposable
{
    private readonly object sync = new();
    private readonly UpdateWorkerProcess worker;
    private readonly byte[] previewRequest;
    private readonly Action release;
    private readonly CancellationTokenRegistration cancelClient, cancelCaller;
    private int commitSent, attempt;
    private bool disposed;
    internal UpdateCleanupPlan Plan { get; }
    internal ulong CommitDeadlineMonotonicMs => Plan.CommitDeadlineMonotonicMs;
    internal bool CommitSent => Volatile.Read(ref commitSent) != 0;

    internal UpdateCleanupSession(UpdateCleanupPlan plan, UpdateWorkerProcess worker, byte[] request, Action release,
        CancellationToken lifetime, CancellationToken caller)
    {
        Plan = plan; this.worker = worker; previewRequest = request; this.release = release;
        cancelClient = lifetime.Register(Dispose); cancelCaller = caller.Register(Dispose);
    }

    internal async Task<UpdateCleanupResult> CommitAsync(CancellationToken cancellation)
    {
        if (Interlocked.CompareExchange(ref attempt, 1, 0) != 0) throw new IOException(Loc.T("Požadavek úklidu se nesmí opakovat."));
        Task<UpdateCleanupResult>? operation = null;
        try {
            lock (sync) {
                ObjectDisposedException.ThrowIf(disposed, this); cancellation.ThrowIfCancellationRequested();
                if (UpdateProtocol.NowMs >= CommitDeadlineMonotonicMs) throw new UpdateException(UpdateStatus.Rejected, Loc.T("Platnost náhledu úklidu vypršela."), 1460);
                Interlocked.Exchange(ref commitSent, 1);
            }
            operation = CommitCoreAsync();
            _ = operation.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await operation.WaitAsync(cancellation).ConfigureAwait(false);
        } catch (Exception error) when (error is IOException or Win32Exception or OperationCanceledException or ObjectDisposedException) {
            return UpdateProtocol.CleanupInterrupted(Plan, CommitSent, error is UpdateException known ? known.Status : UpdateStatus.Unknown,
                error is UpdateException native ? native.Error : 0, error.Message);
        } finally { Dispose(); }
    }
    private async Task<UpdateCleanupResult> CommitCoreAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var request = UpdateProtocol.CleanupCommitRequest(previewRequest, Plan.ManifestHash);
        await worker.WriteAsync(request, true, deadline.Token).ConfigureAwait(false);
        var accepted = await worker.ReadCleanupAsync(request, Plan, deadline.Token).ConfigureAwait(false);
        if (accepted.Frame != UpdateFrame.Accepted || accepted.Status != UpdateStatus.Ok)
            throw new UpdateException(accepted.Status, Loc.T("Přijetí úklidu nebylo potvrzené."), accepted.Error);
        var result = await worker.ReadCleanupAsync(request, Plan, deadline.Token).ConfigureAwait(false);
        if (result.Frame != UpdateFrame.Result) throw new InvalidDataException(Loc.T("Chybí konečný výsledek úklidu."));
        await worker.EndCleanupAsync(result.Status, deadline.Token).ConfigureAwait(false);
        return new(true, result.MayHaveDeleted, result.Status, result.Error, Loc.T("Pracovník dokončil odpověď; výsledek jednotlivých položek je uveden níže."),
            result.Items, UpdateProtocol.CleanupSummary(result.Items, Plan.CompleteInventory));
    }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        _ = Task.Run(RetireAsync);
    }
    private async Task RetireAsync()
    {
        try {
            await worker.RetireAsync(!CommitSent).ConfigureAwait(false);
            cancelClient.Dispose(); cancelCaller.Dispose(); release();
        } catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException) {
            // Retain the single-request lease until an actual exit is known.
        }
    }
}
