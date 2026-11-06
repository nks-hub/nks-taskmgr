using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NksTaskMgr.WinUI;

internal sealed partial class ServiceActionsSession : IDisposable
{
    private readonly object sync = new();
    private SafeFileHandle? job;
    private Process? child;
    private int cancelled;
    private int applySent;
    private ServiceActionItem[] latest = [];
    internal bool ApplySent => Volatile.Read(ref applySent) != 0;
    internal ServiceActionItem[] Latest => Volatile.Read(ref latest);

    internal async Task<ServiceActionPlan> PlanAsync(ServiceAction action, string name, CancellationToken cancellation)
    {
        var request = ServiceActionsProtocol.PlanRequest(action, name);
        var process = Start(cancellation);
        await process.StandardInput.BaseStream.WriteAsync(request, cancellation).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(cancellation).ConfigureAwait(false);
        var (header, payload) = await ReadAsync(process, request, null, cancellation).ConfigureAwait(false);
        return ServiceActionsProtocol.DecodePlan(header, payload, request, name);
    }

    internal async Task<ServiceActionsBatch> ApplyAsync(ServiceActionPlan plan, uint[] approved, IProgress<ServiceActionItem[]>? progress, CancellationToken cancellation)
    {
        Process process; lock (sync) { ObjectDisposedException.ThrowIf(Volatile.Read(ref cancelled) != 0, this); process = child ?? throw new IOException("Pracovník plánu není dostupný."); }
        var request = ServiceActionsProtocol.ApplyRequest(plan, approved);
        Interlocked.Exchange(ref applySent, 1);
        await process.StandardInput.BaseStream.WriteAsync(request, cancellation).ConfigureAwait(false);
        process.StandardInput.Close();
        bool accepted = false;
        for (int frames = 0; frames < 512; ++frames) {
            var (header, payload) = await ReadAsync(process, request, plan, cancellation).ConfigureAwait(false);
            if (header.Phase == ServiceActionPhase.Accepted) {
                if (accepted) throw new InvalidDataException("Opakované přijetí akce služeb.");
                accepted = true; continue;
            }
            if (header.Phase == ServiceActionPhase.Error) throw new ServiceActionException(header.Status, header.Error);
            if (header.Phase == ServiceActionPhase.Progress && !accepted) throw new InvalidDataException("Pracovník nepotvrdil plán před průběhem.");
            var rows = ServiceActionsProtocol.DecodeItems(header, payload, plan, accepted);
            Volatile.Write(ref latest, rows);
            if (header.Phase == ServiceActionPhase.Progress) { progress?.Report((ServiceActionItem[])rows.Clone()); continue; }
            if (header.Phase != ServiceActionPhase.Result) throw new InvalidDataException("Neplatné pořadí odpovědí služeb.");
            var extra = new byte[1];
            if (await process.StandardOutput.BaseStream.ReadAsync(extra, cancellation).ConfigureAwait(false) != 0) throw new InvalidDataException("Data za koncem akce služeb.");
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Pracovník nepotvrdil dokončení odpovědi.");
            return new(plan, header.Status, header.Error, rows, true, true);
        }
        throw new InvalidDataException("Pracovník překročil limit průběžných odpovědí.");
    }

    private static async Task<(ServiceActionHeader Header, byte[] Payload)> ReadAsync(Process process, byte[] request, ServiceActionPlan? plan, CancellationToken cancellation)
    {
        try {
            var bytes = new byte[ServiceActionsProtocol.HeaderBytes];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
            var header = ServiceActionsProtocol.ReadHeader(bytes, request, plan);
            var payload = new byte[header.Payload];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
            return (header, payload);
        } catch (EndOfStreamException error) {
            throw new IOException("Pracovník služeb skončil před dokončením odpovědi.", error);
        }
    }

    private Process Start(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var owned = CreateJobObject(0, 0);
        if (owned.IsInvalid) { int error = Marshal.GetLastPInvokeError(); owned.Dispose(); throw new Win32Exception(error); }
        try {
            var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2108, ActiveProcesses = 1 }, ProcessMemory = 128 * 1024 * 1024 };
            if (!SetInformationJobObject(owned, 9, in limits, (uint)Marshal.SizeOf<JobLimits>())) throw new Win32Exception(Marshal.GetLastPInvokeError());
            lock (sync) {
                cancellation.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(Volatile.Read(ref cancelled) != 0, this); job = owned;
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "nks-service-actions-worker.exe")) {
                    WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, StandardInputEncoding = new UTF8Encoding(false)
                };
                start.ArgumentList.Add("--stdio");
                child = Process.Start(start) ?? throw new IOException("Pracovníka služeb nelze spustit.");
                if (!AssignProcessToJobObject(owned, child.SafeHandle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                return child;
            }
        } catch { owned.Dispose(); Cancel(); throw; }
    }

    internal void Cancel()
    {
        if (Interlocked.Exchange(ref cancelled, 1) != 0) return;
        _ = Task.Run(CancelOwned);
    }
    private void CancelOwned()
    {
        lock (sync) {
            job?.Dispose();
            try { if (child is { HasExited: false }) child.Kill(); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        }
    }
    internal async Task RetireAsync()
    {
        Cancel(); Process? process; lock (sync) process = child;
        if (process is not null) await Task.Run(process.WaitForExit).ConfigureAwait(false);
        Dispose();
    }
    public void Dispose()
    {
        Cancel();
        lock (sync) { if (child is { HasExited: true }) { child.Dispose(); child = null; } job = null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits { public BasicLimits Basic; public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, in JobLimits information, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
