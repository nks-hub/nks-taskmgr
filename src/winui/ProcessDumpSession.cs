using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed partial class ProcessDumpSession : IDisposable
{
    private readonly object sync = new();
    private SafeFileHandle? job;
    private Process? child;
    private bool canceled;
    internal string? CleanupError { get; private set; }

    internal async Task<ProcessDumpResult> CaptureAsync(ProcessDumpTarget target, ProcessDumpMode mode, string path, CancellationToken cancellation)
    {
        ProcessDumpFile? file = null;
        try {
            using var targetHandle = OpenProcess(0x101000, false, target.Key.Pid);
            if (targetHandle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            VerifyTarget(targetHandle, target);
            if (!IsWow64Process2(targetHandle, out ushort machine, out ushort nativeMachine)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var architecture = nativeMachine == 0x8664 ? machine switch { 0x14c => ProcessArchitecture.X86, 0 => ProcessArchitecture.X64, _ => ProcessArchitecture.Unknown } : ProcessArchitecture.Unknown;
            if (architecture == ProcessArchitecture.Unknown) throw new IOException("Architektura cíle není pro dump x86/x64 podporovaná.");
            VerifyTarget(targetHandle, target); cancellation.ThrowIfCancellationRequested();
            file = new ProcessDumpFile(path, target, mode);
            byte[] request = ProcessDumpProtocol.Request(target, mode, file);
            file.Journal!.StartingHelper();
            var process = Start(architecture, cancellation);
            file.Journal.WorkerReady(process, request);
            await process.StandardInput.BaseStream.WriteAsync(request, cancellation).ConfigureAwait(false); process.StandardInput.Close();
            var output = process.StandardOutput.BaseStream;
            var response = new byte[ProcessDumpProtocol.ResponseBytes];
            await output.ReadExactlyAsync(response, cancellation).ConfigureAwait(false);
            var end = new byte[1];
            if (await output.ReadAsync(end, cancellation).ConfigureAwait(false) != 0) throw new InvalidDataException("Pracovník vrátil data za výsledkem dumpu.");
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Pracovník dumpu skončil bez ověřeného výsledku.");
            var result = ProcessDumpProtocol.Response(response, request, architecture, file.Identity);
            cancellation.ThrowIfCancellationRequested();
            if (result.State == ProcessDumpState.Captured) {
                VerifyTarget(targetHandle, target);
                return file.Publish(result, cancellation);
            }
            return result;
        } catch (Exception error) when (cancellation.IsCancellationRequested && error is IOException or Win32Exception or ObjectDisposedException) {
            throw new OperationCanceledException("Čekání na dump bylo zrušeno.", error, cancellation);
        } catch (Win32Exception error) {
            throw new IOException("Dump nebo bezpečné uložení souboru není dostupné (Windows " + error.NativeErrorCode + "): " + error.Message, error);
        } catch (NativeMonitorException error) {
            throw new IOException("Identitu vybraného procesu nelze ověřit.", error);
        } finally {
            Cancel();
            Process? process;
            lock (sync) process = child;
            try {
                if (process is not null) await process.WaitForExitAsync().ConfigureAwait(false);
            } finally {
                try {
                    file?.Dispose();
                    if (file?.ReadyPreserved == true) CleanupError = "Hotový nepublikovaný dump zůstal v přehledu nedokončených dumpů. Obnova je dostupná po ukončení původní aplikace.";
                }
                catch (IOException error) { CleanupError = error.Message; throw; }
            }
        }
    }

    private static void VerifyTarget(SafeProcessHandle process, ProcessDumpTarget target)
    {
        var key = target.Key; var actual = NativeMonitor.QueryProcessIdentity(process, key.Kind);
        if (actual.State != SampleState.Valid || actual.Boot != key.Boot || actual.Pid != key.Pid || actual.Kind != key.Kind || actual.Generation != key.Generation)
            throw new IOException("Identita vybraného procesu se změnila nebo není ověřitelná.");
        if (!GetProcessTimes(process, out ulong created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (created != target.Created100ns || WaitForSingleObject(process, 0) != 258) throw new IOException("Vybraný proces už skončil nebo má jiný čas vytvoření.");
    }

    private Process Start(ProcessArchitecture architecture, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var ownedJob = CreateJobObject(0, 0);
        if (ownedJob.IsInvalid) { int error = Marshal.GetLastPInvokeError(); ownedJob.Dispose(); throw new Win32Exception(error); }
        try {
            var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2108, ActiveProcesses = 1 }, ProcessMemory = 512 * 1024 * 1024 };
            if (!SetInformationJobObject(ownedJob, 9, in limits, (uint)Marshal.SizeOf<JobLimits>())) throw new Win32Exception(Marshal.GetLastPInvokeError());
            lock (sync) {
                cancellation.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(canceled, this); job = ownedJob;
            }
            string executable = architecture == ProcessArchitecture.X86 ? "nks-dump-worker-x86.exe" : "nks-dump-worker.exe";
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, executable)) {
                WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, StandardInputEncoding = new UTF8Encoding(false)
            };
            start.ArgumentList.Add("--stdio");
            var process = Process.Start(start) ?? throw new IOException("Pracovníka dumpu se nepodařilo spustit.");
            lock (sync) { child = process; cancellation.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(canceled, this); }
            if (!AssignProcessToJobObject(ownedJob, process.SafeHandle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return process;
        } catch { ownedJob.Dispose(); Cancel(); throw; }
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
        lock (sync) { if (child is { HasExited: true }) { child.Dispose(); child = null; } job = null; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits { public BasicLimits Basic; public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWow64Process2(SafeProcessHandle process, out ushort machine, out ushort nativeMachine);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out ulong created, out ulong ended, out ulong kernel, out ulong user);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, in JobLimits information, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
