using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed class UpdateWorkerClient : IDisposable
{
    private static int pending;
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private SignedImageLease? guiImage;
    private GuiContext? context;
    private UpdateSession? prepared;
    private bool disposed;
    private sealed record GuiContext(ProcessKey Key, string Version, string Sha256, SignedImageLease.FileId FileId, ulong Bytes);
    internal static bool IsConfigured => PublisherTrust.IsConfigured(PublisherPolicy.Update);
    internal bool IsBusy => Volatile.Read(ref pending) != 0;

    internal Task<UpdateInventory> ListAsync(CancellationToken cancellation = default) => RunAsync(async (pin, token) => {
        var result = await QueryAsync(UpdateCommand.List, pin, null, null, token).ConfigureAwait(false);
        return Inventory(result);
    }, cancellation);

    internal Task<UpdateInventory> CacheAsync(string path, CancellationToken cancellation = default) => RunAsync(async (pin, token) => {
        using var source = SignedImageLease.Open(path, pin);
        PublisherTrust.RequireRole(source, "NKS Task Manager Setup", "NksTaskMgrSetup.exe");
        byte[] hash = source.Hash(UpdateProtocol.MaximumPackageBytes, token);
        var result = await QueryAsync(UpdateCommand.Cache, pin, null, UpdateProtocol.CachePayload(source, hash), token).ConfigureAwait(false);
        if (result.Status != UpdateStatus.Ok) return Inventory(result);
        if (result.Packages.Length != 1 || result.Packages[0].Sha256 != Convert.ToHexString(hash)
            || result.Packages[0].Bytes != source.Length) throw new InvalidDataException("Uložený balíček neodpovídá drženému zdroji.");
        return Inventory(await QueryAsync(UpdateCommand.List, pin, null, null, token).ConfigureAwait(false));
    }, cancellation);

    internal Task<UpdateSession> PrepareAsync(string? packageSha256, CancellationToken cancellation = default)
    {
        bool transferred = false;
        return RunAsync(async (pin, token) => {
            var result = await QueryAsync(UpdateCommand.Plan, pin, packageSha256, null, token).ConfigureAwait(false);
            if (result.Status != UpdateStatus.Ok || result.Plan is not { } plan)
                throw new UpdateException(result.Status, result.Detail.Length > 0 ? result.Detail : UpdateProtocol.StatusText(result.Status), result.Error);
            var gui = Context(pin, token);
            if (plan.Action != (packageSha256 is null ? UpdateAction.Rollback : UpdateAction.Update)
                || packageSha256 is not null && !plan.PackageSha256.Equals(packageSha256, StringComparison.OrdinalIgnoreCase)
                || plan.CurrentVersion != gui.Version || plan.InstalledAppFileId != gui.FileId || plan.InstalledAppBytes != gui.Bytes
                || plan.InstalledAppSha256 != gui.Sha256 || plan.PreviousVersion != plan.CurrentVersion
                || plan.Action == UpdateAction.Rollback && plan.SelectedVersion == plan.CurrentVersion)
                throw new InvalidDataException("Plán neodpovídá výběru ani ověřenému běžícímu obrazu.");
            using (var package = SignedImageLease.Open(plan.PackagePath, pin)) {
                var role = PublisherTrust.RequireRole(package, "NKS Task Manager Setup", "NksTaskMgrSetup.exe");
                if (role.ProductVersion != plan.SelectedVersion || package.Identity != plan.PackageFileId || package.Length != plan.PackageBytes
                    || !CryptographicOperations.FixedTimeEquals(package.Hash(UpdateProtocol.MaximumPackageBytes, token), UpdateProtocol.ParseHash(plan.PackageSha256)))
                    throw new InvalidDataException("Balíček v plánu nebyl shodně ověřený.");
            }
            UpdateWorkerProcess? stage = null;
            try {
                stage = UpdateWorkerProcess.Start(plan.StageHelperPath, pin, gui.Version, plan, token);
                var request = UpdateProtocol.Request(plan.Action == UpdateAction.Update ? UpdateCommand.Apply : UpdateCommand.Rollback,
                    gui.Key, plan.PackageSha256, Convert.FromHexString(plan.PlanId));
                using var abort = token.Register(() => _ = Task.Run(stage.Cancel));
                await stage.WriteAsync(request, false, token).ConfigureAwait(false);
                var ready = await stage.ReadAsync(request, token).ConfigureAwait(false);
                if (ready.Frame != UpdateFrame.Ready || ready.Status != UpdateStatus.Ok || ready.Plan != plan)
                    throw new UpdateException(ready.Status, ready.Detail.Length > 0 ? ready.Detail : "Pracovník nepřipravil stejný plán.", ready.Error);
                token.ThrowIfCancellationRequested();
                var session = new UpdateSession(plan, stage, request, ready.CommitDeadlineMonotonicMs, () => Interlocked.Exchange(ref pending, 0), lifetime.Token, cancellation);
                lock (sync) prepared = session;
                transferred = true; return session;
            } finally {
                if (!transferred && stage is not null) await stage.RetireAsync(true).ConfigureAwait(false);
            }
        }, cancellation, () => transferred);
    }

    private async Task<UpdateResponse> QueryAsync(UpdateCommand command, byte[] pin, string? sha, byte[]? payload, CancellationToken cancellation)
    {
        var gui = Context(pin, cancellation);
        var request = UpdateProtocol.Request(command, gui.Key, sha, payload);
        var worker = UpdateWorkerProcess.Start(Path.Combine(AppContext.BaseDirectory, "nks-update-worker.exe"), pin, gui.Version, null, cancellation);
        using var abort = cancellation.Register(() => _ = Task.Run(worker.Cancel));
        try {
            await worker.WriteAsync(request, true, cancellation).ConfigureAwait(false);
            var result = await worker.ReadAsync(request, cancellation).ConfigureAwait(false);
            if (result.Frame is not (UpdateFrame.Result or UpdateFrame.Rejected)
                || command == UpdateCommand.List && result.Plan is not null
                || command == UpdateCommand.Cache && result.Plan is not null
                || command == UpdateCommand.Plan && (result.Packages.Length != 0 || result.Status == UpdateStatus.Ok && result.Plan is null))
                throw new InvalidDataException("Nesprávná fáze odpovědi aktualizace.");
            await worker.EndAsync(result, cancellation).ConfigureAwait(false); return result;
        } finally { await worker.RetireAsync(true).ConfigureAwait(false); }
    }
    private GuiContext Context(byte[] pin, CancellationToken cancellation)
    {
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); if (context is not null) return context; }
        using var self = Process.GetCurrentProcess();
        var key = NativeMonitor.QueryProcessIdentity(self.SafeHandle, GenerationKind.CreationTime);
        if (key.State != SampleState.Valid || key.Kind != GenerationKind.CreationTime || key.Boot.IsEmpty || key.Generation == 0 || key.Pid != Environment.ProcessId)
            throw new InvalidDataException("Klient aktualizací nemá ověřenou životnost.");
        var image = SignedImageLease.Open(Environment.ProcessPath ?? throw new IOException("Obraz klienta není známý."), pin);
        try {
            image.VerifyProcess(self);
            var role = PublisherTrust.RequireRole(image, "NKS Task Manager", "NksTaskMgr.WinUI.dll");
            string hash = Convert.ToHexString(image.Hash(UpdateProtocol.MaximumPackageBytes, cancellation));
            var value = new GuiContext(new(key.Boot, key.Pid, key.Kind, key.Generation), role.ProductVersion, hash, image.Identity, image.Length);
            cancellation.ThrowIfCancellationRequested();
            lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); guiImage = image; context = value; }
            return value;
        } catch { image.Dispose(); throw; }
    }
    private async Task<T> RunAsync<T>(Func<byte[], CancellationToken, Task<T>> action, CancellationToken cancellation, Func<bool>? transferred = null)
    {
        lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
        var pin = PublisherTrust.ReadPin(PublisherPolicy.Update)
            ?? throw new UpdateException(UpdateStatus.TrustMissing, "Aktualizace nejsou nakonfigurované. Chybí připnutý podpis vydavatele; pracovník nebyl spuštěn.");
        cancellation.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) throw new UpdateException(UpdateStatus.Busy, "Předchozí aktualizace nebo její ukončování ještě probíhá.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token, deadline.Token);
        var token = linked.Token;
        var work = Task.Run(async () => {
            bool peerUnknown = false;
            try { return await action(pin, token).ConfigureAwait(false); }
            catch (UpdatePeerUnresolvedException) { peerUnknown = true; throw; }
            finally { if (!peerUnknown && transferred?.Invoke() != true) Interlocked.Exchange(ref pending, 0); }
        });
        _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await work.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested) {
            throw new UpdateException(UpdateStatus.Unknown, "Čekání na pracovníka vypršelo. Dokud není ověřen jeho konec, nevznikne další požadavek. " + error.Message, 1460);
        }
    }
    private static UpdateInventory Inventory(UpdateResponse response) => new(response.Status, response.Error, response.JournalState, response.Detail, response.Packages);
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        lifetime.Cancel(); prepared?.Dispose(); guiImage?.Dispose();
    }
}
