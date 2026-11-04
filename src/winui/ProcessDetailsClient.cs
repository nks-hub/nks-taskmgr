using System.Collections.Concurrent;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal enum ProcessDetailsFailure { Unavailable, Terminated, InvalidResponse }
internal sealed class ProcessDetailsWorkerException(ProcessDetailsFailure failure, string message, Exception? inner = null) : IOException(message, inner)
{
    internal ProcessDetailsFailure Failure { get; } = failure;
}

internal sealed class ProcessDetailsClient : IDisposable
{
    private readonly SemaphoreSlim available = new(2);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly DetailsSession[] sessions = [new(), new()];
    private readonly ConcurrentQueue<DetailsSession> idle;
    private long nextRequest;
    private int pending;
    private bool disposed;

    internal ProcessDetailsClient() => idle = new(sessions);

    internal Task<ProcessDetailsSnapshot> ReadAsync(ProcessDetailsCommand command, ProcessKey key, ProcessModuleInfo? module, CancellationToken cancellation)
        => ReadCoreAsync(command, key, module, null, false, cancellation);
    internal Task<ProcessDetailsSnapshot> ReadMemoryAsync(ProcessKey key, CancellationToken cancellation)
        => ReadCoreAsync(ProcessDetailsCommand.Memory, key, null, null, true, cancellation);
    internal Task<ProcessDetailsSnapshot> ReadHandlesAsync(ProcessKey key, CancellationToken cancellation)
        => ReadCoreAsync(ProcessDetailsCommand.Handles, key, null, null, true, cancellation);
    internal Task<ProcessDetailsSnapshot> ReadHandleInfoAsync(ProcessKey key, ulong value, CancellationToken cancellation)
        => ReadCoreAsync(ProcessDetailsCommand.HandleInfo, key, null, value, true, cancellation);
    internal Task<ProcessDetailsSnapshot> ReadOwnersAsync(BootIdentifier boot, CancellationToken cancellation)
        => ReadCoreAsync(ProcessDetailsCommand.Owners, new(boot, 0, GenerationKind.SequenceNumber, 0), null, null, true, cancellation);
    internal Task<ProcessDetailsSnapshot> ReadApplicationsAsync(BootIdentifier boot, GenerationKind preference, CancellationToken cancellation)
        => ReadCoreAsync(ProcessDetailsCommand.Applications, new(boot, 0, preference, 0), null, null, true, cancellation);

    internal Task<ProcessDetailsSnapshot> ReadFileSignatureAsync(BootIdentifier boot, string path, CancellationToken cancellation)
        => ReadCoreAsync(ProcessDetailsCommand.FileSignature, new(boot, 0, GenerationKind.SequenceNumber, 0), new(0, 0, "", path), null, true, cancellation);

    private async Task<ProcessDetailsSnapshot> ReadCoreAsync(ProcessDetailsCommand command, ProcessKey key,
        ProcessModuleInfo? module, ulong? handle, bool persistent, CancellationToken cancellation)
    {
        lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.Increment(ref pending) > 4) {
            Interlocked.Decrement(ref pending);
            throw new IOException("Fronta podrobností procesu je obsazená. Zrušte předchozí dotaz nebo chvíli počkejte.");
        }
        bool leased = false, running = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token, deadline.Token);
        DetailsSession? session = null;
        ulong requestId = unchecked((ulong)Interlocked.Increment(ref nextRequest));
        try {
            var request = ProcessDetailsProtocol.Request(command, key, requestId, module, handle);
            await available.WaitAsync(linked.Token).ConfigureAwait(false); leased = true;
            if (!idle.TryDequeue(out session)) throw new InvalidOperationException("A details worker lease was lost.");
            lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
            session.Begin(requestId);
            var owned = session;
            using var stop = linked.Token.Register(() => owned.Cancel(requestId));
            var token = linked.Token;
            // A timed-out caller must not release a worker still finishing its transport.
            var work = Task.Run(async () => {
                try { return await owned.ReadAsync(requestId, request, module, persistent, token).ConfigureAwait(false); }
                finally {
                    try { owned.Complete(requestId); }
                    finally { idle.Enqueue(owned); available.Release(); Interlocked.Decrement(ref pending); }
                }
            });
            running = true;
            _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var result = await work.WaitAsync(linked.Token).ConfigureAwait(false);
            CapabilityReports.Observe(key, result);
            return result;
        } catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested && !lifetime.IsCancellationRequested) {
            CapabilityReports.Failed(command, key, "Vypršel limit dotazu.");
            throw new TimeoutException("Dotaz na podrobnosti překročil limit 5 sekund. Pracovník se ukončuje; dotaz lze zopakovat.", error);
        } catch (IOException error) {
            CapabilityReports.Failed(command, key, error is ProcessDetailsWorkerException { Failure: ProcessDetailsFailure.Terminated } ? "Pracovník předčasně skončil." : "Chyba spojení s pracovníkem.");
            throw;
        } finally {
            if (linked.IsCancellationRequested) session?.Cancel(requestId);
            if (!running) {
                if (session is not null) { session.Complete(requestId); idle.Enqueue(session); }
                if (leased) available.Release();
                Interlocked.Decrement(ref pending);
            }
        }
    }

    public void Dispose()
    {
        lock (sync) {
            if (disposed) return;
            disposed = true; lifetime.Cancel();
            foreach (var session in sessions) session.Dispose();
        }
    }
}
