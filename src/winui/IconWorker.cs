using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NksTaskMgr.WinUI;

internal sealed partial class IconWorker : IDisposable
{
    private const uint Magic = 0x31494b4e;
    private readonly object lifetime = new();
    private Process? process;
    private SafeFileHandle? job;
    private bool stopped;
    internal int ProcessId => process?.Id ?? 0;
    internal string? Failure { get; private set; }

    internal void Start()
    {
        lock (lifetime) {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (job is { IsClosed: false }) return;
        }
        Reset();
        var nextJob = CreateJobObject(0, 0);
        if (nextJob.IsInvalid) { nextJob.Dispose(); throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        Process? next = null;
        try {
            var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(nextJob, 9, in limits, (uint)Marshal.SizeOf<JobLimits>()))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            next = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "nks-icon-worker.exe")) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                StandardInputEncoding = new System.Text.UTF8Encoding(false)
            }) ?? throw new IOException("The executable icon worker did not start.");
            if (!AssignProcessToJobObject(nextJob, next.SafeHandle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            lock (lifetime) {
                ObjectDisposedException.ThrowIf(stopped, this);
                process = next; job = nextJob;
            }
        } catch {
            // A child must not survive a failed job assignment.
            nextJob.Dispose();
            if (next is not null) {
                try { if (!next.HasExited) next.Kill(); } catch (InvalidOperationException) { }
                next.Dispose();
            }
            throw;
        }
    }

    internal byte[]? Request(ProcessKey key)
    {
        Failure = null;
        try {
            Start();
            Span<byte> request = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(request, Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], key.Pid);
            BinaryPrimitives.WriteUInt64LittleEndian(request[8..], key.Generation);
            var input = process!.StandardInput.BaseStream;
            input.Write(request);
            input.Flush();
            return ReadResponse(TimeSpan.FromSeconds(3));
        } catch (Exception error) when (error is IOException or Win32Exception or TimeoutException or ObjectDisposedException) {
            Failure = error.ToString(); Reset(); return null;
        }
    }

    internal byte[]? ReadResponse(TimeSpan timeout)
    {
        Process child; SafeFileHandle ownedJob;
        lock (lifetime) {
            child = process ?? throw new InvalidOperationException("The icon worker has not started.");
            ownedJob = job!;
        }
        int expired = 0;
        var deadline = new Timer(_ => { Interlocked.Exchange(ref expired, 1); ownedJob.Dispose(); }, null, timeout, Timeout.InfiniteTimeSpan);
        try {
            var output = child.StandardOutput.BaseStream;
            Span<byte> response = stackalloc byte[8];
            output.ReadExactly(response);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(response[4..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(response) != Magic || (length != 0 && length != 1350))
                throw new InvalidDataException("The icon worker returned an invalid frame.");
            if (length == 0) return null;
            var bytes = new byte[length];
            output.ReadExactly(bytes);
            return bytes;
        } catch (IOException error) when (Volatile.Read(ref expired) != 0) {
            throw new TimeoutException("The executable icon query exceeded its time limit.", error);
        } finally {
            deadline.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    internal void Stop()
    {
        lock (lifetime) { stopped = true; job?.Dispose(); }
    }

    private void Reset()
    {
        Process? previous; SafeFileHandle? previousJob;
        lock (lifetime) { previous = process; previousJob = job; process = null; job = null; }
        previousJob?.Dispose(); previous?.Dispose();
    }

    public void Dispose() { Stop(); Reset(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
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

internal static class IconWorkers
{
    internal static string? LastFailure { get; private set; }
    private static readonly IconWorker[] instances = [new(), new()];
    private static readonly ConcurrentQueue<IconWorker> idle = new(instances);
    private static readonly SemaphoreSlim available = new(2);

    internal static byte[]? Read(ProcessKey key)
    {
        available.Wait();
        if (!idle.TryDequeue(out var worker)) { available.Release(); throw new InvalidOperationException("An icon worker lease was lost."); }
        try {
            var bytes = worker.Request(key);
            if (worker.Failure is not null) LastFailure = worker.Failure;
            return bytes;
        }
        finally { idle.Enqueue(worker); available.Release(); }
    }

    internal static void Stop() { foreach (var worker in instances) worker.Stop(); }
}
