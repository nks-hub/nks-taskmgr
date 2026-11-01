using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NksTaskMgr.WinUI;

internal sealed partial class DetailsSession : IDisposable
{
    private readonly object sync = new();
    private SafeFileHandle? job;
    private Process? child;
    private ulong activeRequest;
    private bool server, aborted, disposed;

    internal void Begin(ulong requestId)
    {
        lock (sync) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activeRequest != 0) throw new InvalidOperationException("A details worker already has a borrower.");
            activeRequest = requestId; aborted = false;
        }
    }

    internal async Task<ProcessDetailsSnapshot> ReadAsync(ulong requestId, byte[] request, ProcessModuleInfo? module, bool persistent, CancellationToken cancellation)
    {
        try {
            var process = await GetProcessAsync(requestId, persistent, cancellation).ConfigureAwait(false);
            var input = process.StandardInput.BaseStream;
            await input.WriteAsync(request, cancellation).ConfigureAwait(false);
            await input.FlushAsync(cancellation).ConfigureAwait(false);
            if (!persistent) process.StandardInput.Close();
            var output = process.StandardOutput.BaseStream;
            var header = new byte[ProcessDetailsProtocol.ResponseHeaderBytes];
            await output.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
            var payload = new byte[ProcessDetailsProtocol.PayloadLength(header, request)];
            await output.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
            if (!persistent) {
                var end = new byte[1];
                if (await output.ReadAsync(end, cancellation).ConfigureAwait(false) != 0) throw new InvalidDataException("Pracovník vrátil více než jeden datový rámec.");
                await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            }
            var result = ProcessDetailsProtocol.Response(header, payload, request, module);
            if (!persistent && process.ExitCode != 0 && result.State == ProcessDetailsState.Ok)
                throw new ProcessDetailsWorkerException(ProcessDetailsFailure.Terminated, "Pracovník nedokončil dotaz správně.");
            return result;
        } catch (Exception error) when (cancellation.IsCancellationRequested && error is IOException or InvalidDataException or ObjectDisposedException or Win32Exception) {
            Cancel(requestId); throw new OperationCanceledException("Dotaz byl zrušen.", error, cancellation);
        } catch (InvalidDataException error) {
            Cancel(requestId); throw new ProcessDetailsWorkerException(ProcessDetailsFailure.InvalidResponse, "Pracovník vrátil neplatný datový rámec.", error);
        } catch (IOException error) when (error is not ProcessDetailsWorkerException) {
            Cancel(requestId); throw new ProcessDetailsWorkerException(ProcessDetailsFailure.Terminated, "Pracovník skončil před dokončením odpovědi.", error);
        } catch (Win32Exception error) {
            Cancel(requestId); throw new ProcessDetailsWorkerException(ProcessDetailsFailure.Unavailable, "Pracovníka podrobností nelze spustit nebo připojit (Windows " + error.NativeErrorCode + ").", error);
        }
    }

    private async Task<Process> GetProcessAsync(ulong requestId, bool persistent, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (sync) {
            Check(requestId, cancellation);
            if (persistent && server && child is not null && !child.HasExited && job is { IsClosed: false }) return child;
        }
        await ClosePreviousAsync(cancellation).ConfigureAwait(false);
        var ownedJob = CreateJobObject(0, 0);
        if (ownedJob.IsInvalid) { int error = Marshal.GetLastPInvokeError(); ownedJob.Dispose(); throw new Win32Exception(error); }
        Process? process = null;
        try {
            // Kill-on-close, one child, and at most 128 MiB of committed process memory.
            var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2108, ActiveProcesses = 1 }, ProcessMemory = 128 * 1024 * 1024 };
            if (!SetInformationJobObject(ownedJob, 9, in limits, (uint)Marshal.SizeOf<JobLimits>())) throw new Win32Exception(Marshal.GetLastPInvokeError());
            lock (sync) { Check(requestId, cancellation); job = ownedJob; }
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "nks-details-worker.exe")) {
                WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, StandardInputEncoding = new UTF8Encoding(false)
            };
            start.ArgumentList.Add(persistent ? "--stdio-server" : "--stdio");
            process = Process.Start(start) ?? throw new IOException("Pracovníka podrobností se nepodařilo spustit.");
            lock (sync) { child = process; server = persistent; Check(requestId, cancellation); }
            if (!AssignProcessToJobObject(ownedJob, process.SafeHandle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return process;
        } catch {
            // A child must not survive failed startup or a cancellation before job assignment.
            ownedJob.Dispose();
            if (process is not null) {
                Kill(process);
                lock (sync) if (!ReferenceEquals(child, process)) process.Dispose();
            }
            throw;
        }
    }

    private async Task ClosePreviousAsync(CancellationToken cancellation)
    {
        Process? previous;
        lock (sync) { previous = child; job?.Dispose(); if (previous is not null) Kill(previous); }
        if (previous is not null) await previous.WaitForExitAsync(cancellation).ConfigureAwait(false);
        lock (sync) {
            if (ReferenceEquals(child, previous)) { child = null; job?.Dispose(); job = null; previous?.Dispose(); }
        }
    }

    private void Check(ulong requestId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed || aborted || activeRequest != requestId, this);
    }

    internal void Cancel(ulong requestId)
    {
        lock (sync) {
            if (activeRequest != requestId) return;
            aborted = true; job?.Dispose(); if (child is not null) Kill(child);
        }
    }

    internal void Complete(ulong requestId)
    {
        lock (sync) {
            if (activeRequest != requestId) return;
            activeRequest = 0;
            if (child is { HasExited: true }) { child.Dispose(); child = null; job?.Dispose(); job = null; }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
    }

    public void Dispose()
    {
        lock (sync) {
            if (disposed) return;
            disposed = true; job?.Dispose(); if (child is not null) Kill(child);
            if (activeRequest == 0 && child is { HasExited: true }) { child.Dispose(); child = null; job = null; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, in JobLimits information, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
