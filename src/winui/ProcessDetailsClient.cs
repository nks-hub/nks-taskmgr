using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NksTaskMgr.WinUI;

internal sealed class ProcessDetailsClient : IDisposable
{
    private readonly SemaphoreSlim available = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private DetailsOperation? active;
    private long nextRequest;
    private int pending;
    private bool disposed;

    internal async Task<ProcessDetailsSnapshot> ReadAsync(ProcessDetailsCommand command, ProcessKey key,
        ProcessModuleInfo? module, CancellationToken cancellation)
    {
        lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.Increment(ref pending) > 3) {
            Interlocked.Decrement(ref pending);
            throw new IOException("Fronta podrobností procesu je obsazená. Zrušte předchozí dotaz nebo chvíli počkejte.");
        }
        bool leased = false, running = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token, deadline.Token);
        DetailsOperation? operation = null;
        try {
            var request = ProcessDetailsProtocol.Request(command, key, unchecked((ulong)Interlocked.Increment(ref nextRequest)), module);
            await available.WaitAsync(linked.Token).ConfigureAwait(false); leased = true;
            operation = new DetailsOperation();
            lock (sync) {
                ObjectDisposedException.ThrowIf(disposed, this);
                active = operation;
            }
            using var stop = linked.Token.Register(operation.Stop);
            var owned = operation;
            var token = linked.Token;
            // Keep the lease until child cleanup finishes, even after a caller timeout.
            var work = Task.Run(async () => {
                try { return await owned.ReadAsync(request, module, token).ConfigureAwait(false); }
                finally {
                    owned.Dispose();
                    lock (sync) if (ReferenceEquals(active, owned)) active = null;
                    available.Release(); Interlocked.Decrement(ref pending);
                }
            });
            running = true;
            _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await work.WaitAsync(linked.Token).ConfigureAwait(false);
        } catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested && !lifetime.IsCancellationRequested) {
            throw new TimeoutException("Dotaz na podrobnosti překročil limit 5 sekund. Pracovník se ukončuje; dotaz lze zopakovat.", error);
        } finally {
            if (linked.IsCancellationRequested) operation?.Stop();
            if (!running) {
                operation?.Dispose();
                lock (sync) if (ReferenceEquals(active, operation)) active = null;
                if (leased) available.Release();
                Interlocked.Decrement(ref pending);
            }
        }
    }

    public void Dispose()
    {
        lock (sync) {
            if (disposed) return;
            disposed = true; lifetime.Cancel(); active?.Stop();
        }
    }
}

internal sealed partial class DetailsOperation : IDisposable
{
    private readonly object sync = new();
    private SafeFileHandle? job;
    private Process? child;
    private bool stopped;

    internal async Task<ProcessDetailsSnapshot> ReadAsync(byte[] request, ProcessModuleInfo? module, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var ownedJob = CreateJobObject(0, 0);
        if (ownedJob.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        // Kill-on-close, one child, and at most 128 MiB of committed process memory.
        var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2108, ActiveProcesses = 1 }, ProcessMemory = 128 * 1024 * 1024 };
        if (!SetInformationJobObject(ownedJob, 9, in limits, (uint)Marshal.SizeOf<JobLimits>())) throw new Win32Exception(Marshal.GetLastPInvokeError());
        lock (sync) {
            cancellation.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(stopped, this); job = ownedJob;
        }
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "nks-details-worker.exe")) {
            WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, StandardInputEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("--stdio");
        using var process = Process.Start(start) ?? throw new IOException("Pracovníka podrobností se nepodařilo spustit.");
        try {
            lock (sync) {
                if (stopped || cancellation.IsCancellationRequested) { Kill(process); cancellation.ThrowIfCancellationRequested(); throw new ObjectDisposedException(nameof(DetailsOperation)); }
                child = process;
            }
            if (!AssignProcessToJobObject(ownedJob, process.SafeHandle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var input = process.StandardInput.BaseStream;
            await input.WriteAsync(request, cancellation).ConfigureAwait(false);
            await input.FlushAsync(cancellation).ConfigureAwait(false);
            process.StandardInput.Close();
            var output = process.StandardOutput.BaseStream;
            var header = new byte[ProcessDetailsProtocol.ResponseHeaderBytes];
            await output.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
            var payload = new byte[ProcessDetailsProtocol.PayloadLength(header, request)];
            await output.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
            var end = new byte[1];
            if (await output.ReadAsync(end, cancellation).ConfigureAwait(false) != 0) throw new InvalidDataException("Pracovník vrátil více než jeden datový rámec.");
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            var result = ProcessDetailsProtocol.Response(header, payload, request, module);
            if (process.ExitCode != 0 && result.State == ProcessDetailsState.Ok) throw new IOException("Pracovník nedokončil dotaz správně.");
            return result;
        } finally {
            lock (sync) { Kill(process); child = null; job = null; }
        }
    }

    internal void Stop()
    {
        lock (sync) {
            stopped = true; job?.Dispose();
            if (child is not null) Kill(child);
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
    }

    public void Dispose() => Stop();

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
