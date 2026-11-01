using System.ComponentModel;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal enum DumpRecoveryState { Blocked, Partial, Ready, Published, Missing }
internal enum DumpRecoveryAction { Complete, DeleteTemporary, RemoveJournal }
internal sealed record DumpRecoveryEntry(DumpRecoveryDocument Document, DumpRecoveryState State, ulong Bytes, string Status)
{
    public override string ToString() => (Document.Record is { } record ? Path.GetFileName(record.Destination) : Document.Name) + " · " + Status;
}

internal sealed class DumpRecoveryClient : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private int pending;
    internal bool IsBusy => Volatile.Read(ref pending) != 0;
    internal Task<DumpRecoveryEntry[]> LoadAsync(CancellationToken cancellation) => RunAsync(token => DumpRecoveryStore.ReadAll(token).Select(document => {
        token.ThrowIfCancellationRequested(); return Inspect(document);
    }).ToArray(), cancellation);
    internal Task<string> ActAsync(DumpRecoveryEntry entry, DumpRecoveryAction action, CancellationToken cancellation) => RunAsync(token => {
        if (entry.Document.Record is not { } original) throw new IOException("Neověřený záznam nelze měnit.");
        var document = DumpRecoveryStore.Reload(original.Id, entry.Document.Proof);
        var record = document.Record!;
        var current = Inspect(document);
        token.ThrowIfCancellationRequested();
        if (action == DumpRecoveryAction.RemoveJournal && current.State == DumpRecoveryState.Missing) {
            DumpRecoveryProcesses.EnsureStopped(record); DumpRecoveryStore.Remove(record.Id, document.Proof);
            return "Záznam bez souboru byl odstraněn. Žádný datový soubor se neměnil.";
        }
        if (action == DumpRecoveryAction.DeleteTemporary && current.State is DumpRecoveryState.Partial or DumpRecoveryState.Ready) {
            using var file = ProcessDumpFile.Reopen(record, document.Proof, false, true);
            token.ThrowIfCancellationRequested(); file.DeleteRecovered(record);
            return "Ověřený dočasný soubor a jeho záznam byly odstraněny.";
        }
        if (action != DumpRecoveryAction.Complete || current.State is not (DumpRecoveryState.Ready or DumpRecoveryState.Published))
            throw new IOException("Stav transakce neumožňuje požadovanou změnu. Soubor zůstane zachovaný.");
        bool published = current.State == DumpRecoveryState.Published;
        using var ready = ProcessDumpFile.Reopen(record, document.Proof, published, !published);
        if ((ulong)RandomAccess.GetLength(ready.Handle) != record.FileBytes || ready.Hash(token) != record.Sha256)
            throw new IOException("Velikost nebo SHA-256 nesouhlasí s hotovým dumpem. Soubor ani záznam se nebudou měnit.");
        token.ThrowIfCancellationRequested(); DumpRecoveryProcesses.EnsureStopped(record); ready.VerifyOwned(record);
        if (published) {
            DumpRecoveryStore.Remove(record.Id, document.Proof);
            return "Již uložený dump byl ověřen. Odstranil se pouze dokončený záznam obnovy.";
        }
        var result = ready.Publish(new(ProcessDumpState.Captured, 0, record.CaptureFlags, record.Architecture,
            record.FileBytes, record.Target.Created100ns, record.Sha256, record.FileId), token);
        return result.RecoveryWarning ?? "Hotový dump byl ověřen a uložen bez přepsání existujícího souboru.";
    }, cancellation);

    private static DumpRecoveryEntry Inspect(DumpRecoveryDocument document)
    {
        if (document.Record is not { } record) return new(document, DumpRecoveryState.Blocked, 0, document.Error);
        try {
            DumpRecoveryProcesses.EnsureStopped(record);
            if (record.Phase == DumpRecoveryPhase.Intent) return new(document, DumpRecoveryState.Blocked, 0, "Identita dočasného souboru nebyla zapsaná. Soubor se nebude hledat ani měnit.");
            using var parent = ProcessDumpFile.HoldRecoveryParent(record);
            try {
                using var file = ProcessDumpFile.Reopen(record, document.Proof, false, false);
                long size = RandomAccess.GetLength(file.Handle);
                if (size < 0 || (ulong)size > ProcessDumpProtocol.MaximumFileBytes) throw new IOException("Soubor překračuje limit ověřování.");
                return new(document, record.Phase == DumpRecoveryPhase.Ready ? DumpRecoveryState.Ready : DumpRecoveryState.Partial, (ulong)size,
                    record.Phase == DumpRecoveryPhase.Ready ? "Zapsaný hotový dump; SHA-256 se ověří před dokončením." : "Nedokončený dočasný soubor s ověřenou identitou.");
            } catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) {
                try {
                    using var file = ProcessDumpFile.Reopen(record, document.Proof, true, false);
                    if (record.Phase != DumpRecoveryPhase.Ready) throw new IOException("Cílový soubor nemá uložený důkaz dokončení. Zůstane zachovaný.");
                    long length = RandomAccess.GetLength(file.Handle);
                    if (length < 0 || (ulong)length > ProcessDumpProtocol.MaximumFileBytes) throw new IOException("Velikost souboru překračuje limit ověřování.");
                    return new(document, DumpRecoveryState.Published, (ulong)length, "Stejný soubor už má finální název. Lze ověřit jen dokončení záznamu.");
                } catch (Win32Exception absent) when (absent.NativeErrorCode is 2 or 3) {
                    return new(document, DumpRecoveryState.Missing, 0, "Dočasný ani cílový soubor nejsou přítomné. Lze potvrzeně odstranit jen záznam.");
                }
            }
        } catch (Exception error) when (error is IOException or Win32Exception or NativeMonitorException or UnauthorizedAccessException or ArgumentException) {
            return new(document, DumpRecoveryState.Blocked, 0, "Bezpečnou změnu nelze ověřit. " + error.Message);
        }
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, T> action, CancellationToken cancellation)
    {
        if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) throw new IOException("Předchozí čtení nebo obnova ještě probíhá.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = linked.Token; bool started = false;
        try {
            var work = Task.Run(async () => {
                try { using var io = await DumpRecoveryIo.EnterAsync(token).ConfigureAwait(false); return action(token); }
                finally { Interlocked.Exchange(ref pending, 0); }
            });
            started = true;
            _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await work.WaitAsync(token).ConfigureAwait(false);
        } catch (System.Text.Json.JsonException error) {
            throw new IOException("Záznam obnovy je poškozený. Soubor ani záznam se nebudou měnit.", error);
        } finally { if (!started) Interlocked.Exchange(ref pending, 0); }
    }
    public void Dispose() => lifetime.Cancel();
}
