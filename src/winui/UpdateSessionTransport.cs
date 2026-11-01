using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace NksTaskMgr.WinUI;

internal sealed partial class UpdateWorkerProcess
{
    private readonly Process process;
    private readonly SignedImageLease image;
    private SafeFileHandle? job;
    private UpdateWorkerProcess(Process process, SignedImageLease image, SafeFileHandle? job)
    { this.process = process; this.image = image; this.job = job; }

    internal static UpdateWorkerProcess Start(string path, byte[] pin, string currentVersion, UpdatePlan? plan, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var image = SignedImageLease.Open(path, pin); Process? child = null; SafeFileHandle? job = null;
        try {
            var role = PublisherTrust.RequireRole(image, "NKS Task Manager Update Worker", "nks-update-worker.exe");
            if (role.ProductVersion != currentVersion) throw new InvalidDataException("Verze podepsaného pracovníka neodpovídá běžícímu klientovi.");
            if (plan is not null && (image.Identity != plan.StageHelperFileId || image.Length != plan.StageHelperBytes
                || !CryptographicOperations.FixedTimeEquals(image.Hash(UpdateProtocol.MaximumPackageBytes, cancellation), UpdateProtocol.ParseHash(plan.StageHelperSha256))))
                throw new InvalidDataException("Připravený pracovník neodpovídá schválenému plánu.");
            if (plan is null) {
                job = CreateJobObject(0, 0);
                if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
                var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2108, ActiveProcesses = 1 }, ProcessMemory = 128 * 1024 * 1024 };
                if (!SetInformationJobObject(job, 9, in limits, (uint)Marshal.SizeOf<JobLimits>())) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            cancellation.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(image.Path) {
                WorkingDirectory = System.IO.Path.GetDirectoryName(image.Path)!, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true
            };
            start.ArgumentList.Add("--stdio");
            child = Process.Start(start) ?? throw new IOException("Pracovníka aktualizací nelze spustit.");
            if (job is not null && !AssignProcessToJobObject(job, child.SafeHandle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            image.VerifyProcess(child); cancellation.ThrowIfCancellationRequested();
            return new(child, image, job);
        } catch {
            job?.Dispose();
            if (child is not null) {
                TryKill(child);
                try { child.WaitForExit(); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) {
                    throw new UpdatePeerUnresolvedException("Ukončení odmítnutého pracovníka nelze ověřit.", error);
                }
                child.Dispose(); image.Dispose();
            }
            else image.Dispose();
            throw;
        }
    }
    internal async Task WriteAsync(byte[] request, bool close, CancellationToken cancellation)
    {
        await process.StandardInput.BaseStream.WriteAsync(request, cancellation).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(cancellation).ConfigureAwait(false);
        if (close) process.StandardInput.Close();
    }
    internal async Task<UpdateResponse> ReadAsync(byte[] request, CancellationToken cancellation)
    {
        var header = new byte[UpdateProtocol.HeaderBytes];
        await process.StandardOutput.BaseStream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        var payload = new byte[UpdateProtocol.PayloadLength(header, request)];
        await process.StandardOutput.BaseStream.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
        return UpdateProtocol.Decode(header, payload, request);
    }
    internal async Task EndAsync(UpdateResponse response, CancellationToken cancellation)
    {
        var extra = new byte[1];
        if (await process.StandardOutput.BaseStream.ReadAsync(extra, cancellation).ConfigureAwait(false) != 0) throw new InvalidDataException("Data za koncem odpovědi aktualizací.");
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        if (response.Status == UpdateStatus.Ok && process.ExitCode != 0) throw new IOException("Pracovník nepotvrdil úspěšné dokončení odpovědi.");
    }
    internal void Cancel()
    {
        job?.Dispose(); TryKill(process);
    }
    internal async Task RetireAsync(bool kill)
    {
        if (kill) Cancel();
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) {
            throw new UpdatePeerUnresolvedException("Skutečné ukončení pracovníka aktualizací nelze ověřit.", error);
        }
        job?.Dispose(); job = null; process.Dispose(); image.Dispose();
    }
    private static void TryKill(Process child)
    {
        try { if (!child.HasExited) child.Kill(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits { public BasicLimits Basic; public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int kind, in JobLimits info, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
