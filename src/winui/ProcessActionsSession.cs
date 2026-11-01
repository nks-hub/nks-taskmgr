using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NksTaskMgr.WinUI;

internal sealed partial class ProcessActionsSession : IDisposable
{
    private readonly object sync = new();
    private SafeFileHandle? job;
    private Process? child;
    private bool canceled;
    private readonly bool elevated;
    private ProcessTreeActionsPlan? treePlan;
    internal ProcessTreeActionsPlan? TreePlan => Volatile.Read(ref treePlan);
    internal ProcessActionsSession(bool elevated = false) => this.elevated = elevated;

    internal async Task<ProcessActionsBatch> ExecuteAsync(byte[] request, CancellationToken cancellation)
    {
        try {
            var process = Start(cancellation);
            await process.StandardInput.BaseStream.WriteAsync(request, cancellation).ConfigureAwait(false);
            process.StandardInput.Close();
            var output = process.StandardOutput.BaseStream;
            var header = new byte[ProcessActionsProtocol.ResponseHeaderBytes];
            await output.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
            _ = ProcessActionsProtocol.PayloadLength(header, request);
            bool accepted = ProcessActionsProtocol.Frame(header) == ProcessActionFrame.Accepted;
            if (accepted) await output.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
            else if (ProcessActionsProtocol.Frame(header) is ProcessActionFrame.Results or ProcessActionFrame.CpuSets)
                throw new InvalidDataException("Pracovník neověřil přijetí požadavku před výsledkem.");
            bool tree = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(6)) == (ushort)ProcessActionCommand.TerminateTree;
            int plans = 0;
            while (tree && ProcessActionsProtocol.Frame(header) == ProcessActionFrame.TreePlan) {
                if (!accepted || ++plans > 3) throw new InvalidDataException("Neplatná posloupnost plánů stromu.");
                var bytes = new byte[ProcessActionsProtocol.PayloadLength(header, request)];
                await output.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
                var plan = ProcessTreeActionsProtocol.Plan(bytes, request, checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12))), TreePlan, false);
                Volatile.Write(ref treePlan, plan);
                await output.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
            }
            var payload = new byte[ProcessActionsProtocol.PayloadLength(header, request)];
            await output.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
            var result = tree && ProcessActionsProtocol.Frame(header) == ProcessActionFrame.TreeResults
                ? ProcessTreeActionsProtocol.Results(header, payload, request, TreePlan ?? throw new InvalidDataException("Chybí doložený plán stromu."))
                : ProcessActionsProtocol.Response(header, payload, request) with { Accepted = accepted, Tree = TreePlan };
            if (accepted && result.Frame == ProcessActionFrame.Rejected)
                throw new InvalidDataException("Pracovník odmítl požadavek až po potvrzení přijetí.");
            var end = new byte[1];
            if (await output.ReadAsync(end, cancellation).ConfigureAwait(false) != 0)
                throw new InvalidDataException("Pracovník vrátil data za koncem výsledku.");
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            if (process.ExitCode != 0 && result.Frame is ProcessActionFrame.Results or ProcessActionFrame.TreeResults or ProcessActionFrame.CpuSets)
                throw new IOException("Pracovník skončil bez potvrzeného výsledku akce.");
            return result;
        } catch (Exception error) when (cancellation.IsCancellationRequested && error is IOException or InvalidDataException or ObjectDisposedException or Win32Exception) {
            throw new OperationCanceledException("Čekání bylo zrušeno. Výsledek rozpracované akce není známý.", error, cancellation);
        } catch (Win32Exception error) {
            throw new IOException("Pracovníka akcí nelze spustit nebo připojit (Windows " + error.NativeErrorCode + ").", error);
        } finally {
            Cancel();
            Process? process;
            lock (sync) process = child;
            // Keep the single-request lease until the owned child has actually exited.
            if (process is not null) await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private Process Start(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var trusted = elevated ? ElevationBrokerTrust.Open() : null;
        cancellation.ThrowIfCancellationRequested();
        var ownedJob = CreateJobObject(0, 0);
        if (ownedJob.IsInvalid) { int error = Marshal.GetLastPInvokeError(); ownedJob.Dispose(); throw new Win32Exception(error); }
        try {
            var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2108, ActiveProcesses = 1 }, ProcessMemory = 128 * 1024 * 1024 };
            if (!SetInformationJobObject(ownedJob, 9, in limits, (uint)Marshal.SizeOf<JobLimits>()))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            lock (sync) {
                cancellation.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(canceled, this);
                job = ownedJob;
                var start = new ProcessStartInfo(trusted?.Path ?? Path.Combine(AppContext.BaseDirectory, "nks-actions-worker.exe")) {
                    WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, StandardInputEncoding = new UTF8Encoding(false)
                };
                start.ArgumentList.Add("--stdio");
                child = Process.Start(start) ?? throw new IOException("Pracovníka akcí se nepodařilo spustit.");
                if (!AssignProcessToJobObject(ownedJob, child.SafeHandle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                trusted?.VerifyProcess(child);
                return child;
            }
        } catch {
            ownedJob.Dispose(); Cancel(); throw;
        }
    }

    internal void Cancel()
    {
        lock (sync) {
            canceled = true; job?.Dispose();
            try { if (child is { HasExited: false }) child.Kill(); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        }
    }

    public void Dispose()
    {
        Cancel();
        lock (sync) {
            if (child is { HasExited: true }) { child.Dispose(); child = null; }
            job = null;
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
