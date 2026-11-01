using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static partial class ProcessDumpQaChecks
{
    private const string Ready = "NKS_DUMP_READY";
    private sealed class OwnedChild(Process process, ProcessArchitecture architecture)
    {
        internal readonly Process Process = process;
        internal SafeProcessHandle Handle => Process.SafeHandle;
        internal readonly ProcessArchitecture Architecture = architecture;
        internal ProcessDumpTarget Target = null!;
        internal bool Assigned;
        internal Task<string?>? PendingLine;
        internal Task<string?> ReadLine() {
            var task = Process.StandardOutput.ReadLineAsync(); PendingLine = task;
            _ = task.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    internal static Task<bool> RunAsync(string privateDirectory, Action<string> log, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(log);
        return Task.Run(() => RunCoreAsync(privateDirectory, log, cancellation));
    }

    private static async Task<bool> RunCoreAsync(string privateDirectory, Action<string> log, CancellationToken cancellation)
    {
        var children = new List<OwnedChild>(2);
        using var client = new ProcessDumpClient();
        SafeFileHandle? childJob = null;
        ProcessDumpFile? parentLease = null, runLease = null;
        bool passed = false, cleanup = true;
        int captures = 0;
        string? runDirectory = null;
        try {
            cancellation.ThrowIfCancellationRequested();
            CheckOrdinaryToken();
            Require(Environment.Is64BitProcess && Environment.Is64BitOperatingSystem, "Dump QA requires the actual x64 GUI on x64 Windows.");
            string baseDirectory = Path.GetFullPath(privateDirectory);
            Require(baseDirectory.Length >= 3 && baseDirectory[1] == ':' && baseDirectory[2] == '\\', "Dump QA requires a local private directory.");
            parentLease = ProcessDumpFile.HoldDirectory(baseDirectory);
            runDirectory = Path.Combine(baseDirectory, "dump-qa-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runDirectory);
            runLease = ProcessDumpFile.HoldDirectory(runDirectory);
            Require(!Directory.EnumerateFileSystemEntries(runDirectory).Any(), "The new private QA directory is not empty.");
            using var gui = Process.GetCurrentProcess();
            var creator = DumpRecoveryProcesses.Current();
            log($"dump-qa begin;guiPid={gui.Id};session={gui.SessionId};elevated=False;aot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported};directory={runDirectory}");
            foreach (string worker in new[] { "nks-dump-worker.exe", "nks-dump-worker-x86.exe" }) {
                string path = Path.Combine(AppContext.BaseDirectory, worker);
                Require(File.Exists(path), "A production dump helper is absent: " + worker);
                using var file = File.OpenRead(path);
                log($"dump-qa helper={worker};sha256={Convert.ToHexString(SHA256.HashData(file))}");
            }
            childJob = CreateJobObject(0, 0);
            if (childJob.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Create own dump fixture job");
            var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(childJob, 9, in limits, (uint)Marshal.SizeOf<JobLimits>()))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Configure own dump fixture job");

            foreach (var architecture in new[] { ProcessArchitecture.X64, ProcessArchitecture.X86 }) {
                var child = await StartChildAsync(architecture, childJob, children, creator, log, cancellation).ConfigureAwait(false);
                foreach (var mode in new[] { ProcessDumpMode.Mini, ProcessDumpMode.Full }) {
                    CheckOriginal(child);
                    string destination = Path.Combine(runDirectory, (architecture == ProcessArchitecture.X64 ? "x64" : "wow64") + "-" + mode.ToString().ToLowerInvariant() + ".dmp");
                    var result = await client.CaptureAsync(child.Target, mode, destination, cancellation).ConfigureAwait(false);
                    cleanup &= await WaitIdleAsync(client, log).ConfigureAwait(false);
                    Require(result.State == ProcessDumpState.Captured && result.Error == 0 && result.SavedPath is not null
                        && Path.GetFullPath(result.SavedPath) == Path.GetFullPath(destination)
                        && result.ObservedCreated100ns == child.Target.Created100ns && result.Architecture == architecture,
                        "Production client did not publish the exact requested child dump.");
                    Require(result.RecoveryWarning is null && client.CleanupError is null, "Capture retained a recovery warning; cleanup is not complete.");
                    var inspected = ProcessDumpQaReader.Inspect(destination, child.Target, mode, architecture, result, cancellation);
                    CheckOriginal(child);
                    await CheckNoOwnedJournalsAsync(runDirectory, cancellation).ConfigureAwait(false);
                    ++captures;
                    log($"dump-qa captured;architecture={architecture};mode={mode};pid={child.Target.Key.Pid};kind={child.Target.Key.Kind};generation={child.Target.Key.Generation};rawCT={child.Target.Created100ns};flags=0x{result.CaptureFlags:X};bytes={inspected.Bytes};sha256={inspected.Sha256};threads={inspected.Threads};modules={inspected.Modules};memoryRanges={inspected.MemoryRanges};processIdInDump={inspected.ProcessIdPresent};streams={inspected.Streams};file={destination}");
                }
            }

            var own = children[0];
            foreach (bool wrongRaw in new[] { false, true }) {
                CheckOriginal(own);
                var target = wrongRaw ? own.Target with { Created100ns = own.Target.Created100ns + 1 }
                    : own.Target with { Key = own.Target.Key with { Generation = own.Target.Key.Generation + 1 } };
                string path = Path.Combine(runDirectory, wrongRaw ? "wrong-raw.dmp" : "wrong-generation.dmp");
                bool refused = false;
                try { _ = await client.CaptureAsync(target, ProcessDumpMode.Mini, path, cancellation).ConfigureAwait(false); }
                catch (IOException) { refused = true; }
                cleanup &= await WaitIdleAsync(client, log).ConfigureAwait(false);
                Require(refused && !File.Exists(path) && client.CleanupError is null, "A mismatching full generation/raw FILETIME produced a dump or unconfirmed cleanup.");
                CheckOriginal(own);
                await CheckNoOwnedJournalsAsync(runDirectory, cancellation).ConfigureAwait(false);
                log("dump-qa mismatch-refused;field=" + (wrongRaw ? "rawCreated" : "generation") + ";saved=False;childStillOriginal=True");
            }

            string sentinel = Path.Combine(runDirectory, "existing-sentinel.dmp");
            byte[] content = "NKS dump QA existing file must remain unchanged\r\n"u8.ToArray();
            using (var file = new FileStream(sentinel, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                file.Write(content); file.Flush(true);
            }
            DumpFileIdentity sentinelIdentity;
            using (var file = File.OpenHandle(sentinel)) sentinelIdentity = ProcessDumpFile.FileIdentity(file);
            bool existingRefused = false;
            try { _ = await client.CaptureAsync(own.Target, ProcessDumpMode.Mini, sentinel, cancellation).ConfigureAwait(false); }
            catch (IOException) { existingRefused = true; }
            cleanup &= await WaitIdleAsync(client, log).ConfigureAwait(false);
            using (var file = File.OpenHandle(sentinel)) Require(ProcessDumpFile.FileIdentity(file) == sentinelIdentity, "Existing sentinel identity changed.");
            Require(existingRefused && client.CleanupError is null && File.ReadAllBytes(sentinel).SequenceEqual(content), "Existing destination was overwritten or accepted, or cleanup was not confirmed.");
            CheckOriginal(own);
            await CheckNoOwnedJournalsAsync(runDirectory, cancellation).ConfigureAwait(false);
            log("dump-qa existing-destination-refused;sentinelIdentityPreserved=True;sentinelBytesPreserved=True");

            string canceledPath = Path.Combine(runDirectory, "pre-canceled.dmp");
            using (var beforeStart = new CancellationTokenSource()) {
                beforeStart.Cancel(); bool canceled = false;
                try { _ = await client.CaptureAsync(own.Target, ProcessDumpMode.Full, canceledPath, beforeStart.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { canceled = true; }
                cleanup &= await WaitIdleAsync(client, log).ConfigureAwait(false);
                Require(canceled && !File.Exists(canceledPath) && client.CleanupError is null, "Pre-cancel was not honored without a saved dump.");
            }
            CheckOriginal(own);
            await CheckNoOwnedJournalsAsync(runDirectory, cancellation).ConfigureAwait(false);
            log("dump-qa pre-cancel=True;workerLease=False;ownedTemporary=False;ownedJournal=False");
            Require(captures == 4, "Not all four actual architecture/mode combinations were validated.");
            log("NOT_COVERED dump-qa inflight-cancel;small real dumps have no deterministic in-capture gate;noTimerRaceClaim=True");
            log("NOT_COVERED dump-qa debugger-open/unwind;independent structural inspection is not debugger validation.");
            log("NOT_COVERED dump-qa confirmation-dialog/SavePicker/pixel-layout;this harness calls the production client.");
            passed = true;
        } catch (Exception error) {
            log("FAIL dump-qa " + error);
        } finally {
            client.Dispose();
            cleanup &= await WaitIdleAsync(client, log).ConfigureAwait(false);
            foreach (var child in children) {
                try {
                    if (WaitForSingleObject(child.Handle, 0) == 258) {
                        await child.Process.StandardInput.WriteLineAsync("finish").WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                        await child.Process.StandardInput.FlushAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                } catch (Exception error) { cleanup = false; log("FAIL dump-qa graceful own-child close " + error.Message); }
            }
            // The job contains only fixture processes created above, never the GUI or a PID lookup.
            foreach (var child in children.Where(child => !child.Assigned)) {
                if (WaitForSingleObject(child.Handle, 0) == 258 && !TerminateProcess(child.Handle, 0xffff)) {
                    cleanup = false; log("FAIL dump-qa unassigned own-child abort " + Marshal.GetLastPInvokeError());
                }
            }
            if (children.Count > 0) await Task.Delay(100).ConfigureAwait(false);
            childJob?.Dispose(); childJob = null;
            long began = Stopwatch.GetTimestamp(); bool warned = false;
            while (children.Any(child => WaitForSingleObject(child.Handle, 0) != 0)) {
                if (!warned && Stopwatch.GetElapsedTime(began) > TimeSpan.FromSeconds(5)) {
                    warned = true; cleanup = false;
                    log("FAIL dump-qa own-child exit not confirmed;keeping GUI parent alive and original handles held.");
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            foreach (var child in children) child.Process.Dispose();
            if (runDirectory is not null) {
                try { await CheckNoOwnedJournalsAsync(runDirectory, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) { cleanup = false; log("FAIL dump-qa final transaction cleanup " + error.Message); }
            }
            runLease?.Dispose(); parentLease?.Dispose();
            passed &= cleanup && !cancellation.IsCancellationRequested;
            log($"{(passed ? "PASS" : "FAIL")} dump-qa finished;passed={passed};captures={captures};cleanup={cleanup};cancelled={cancellation.IsCancellationRequested};inflightCancelCovered=False;debuggerCovered=False;dialogsCovered=False;artifactsPreserved=True");
        }
        return passed;
    }

    private static async Task<OwnedChild> StartChildAsync(ProcessArchitecture architecture, SafeFileHandle job,
        List<OwnedChild> children, DumpCreator creator, Action<string> log, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string directory = architecture == ProcessArchitecture.X64 ? Environment.SystemDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");
        string image = Path.Combine(directory, "cmd.exe");
        Require(File.Exists(image), "The requested real cmd fixture is unavailable: " + architecture);
        var start = new ProcessStartInfo(image) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "/d", "/q", "/c", "echo NKS_DUMP_READY&set /p _nks_dump_gate=&exit /b 42" }) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new IOException("Own cmd fixture did not start.");
        var child = new OwnedChild(process, architecture); children.Add(child);
        if (!AssignProcessToJobObject(job, child.Handle)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Assign own cmd fixture job");
        child.Assigned = true;
        Require(GetProcessTimes(child.Handle, out ulong created, out _, out _, out _) && created != 0, "Independent own-child raw FILETIME is unavailable.");
        var identity = NativeMonitor.QueryProcessIdentity(child.Handle, GenerationKind.SequenceNumber);
        if (identity.State is SampleState.Unsupported or SampleState.Unavailable)
            identity = NativeMonitor.QueryProcessIdentity(child.Handle, GenerationKind.CreationTime);
        Require(identity.State == SampleState.Valid && identity.Kind is GenerationKind.CreationTime or GenerationKind.SequenceNumber
            && identity.Generation != 0 && identity.Pid == (uint)process.Id && identity.Pid != (uint)Environment.ProcessId
            && identity.Boot == creator.Boot && !identity.Boot.IsEmpty
            && (identity.Kind != GenerationKind.CreationTime || identity.Generation == created), "Own child's full identity is not verifiable.");
        child.Target = new(new(identity.Boot, identity.Pid, identity.Kind, identity.Generation), created);
        Require(await child.ReadLine().WaitAsync(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false) == Ready,
            "The actual cmd fixture did not enter its stdin gate.");
        CheckOriginal(child);
        log($"dump-qa own-child;pid={identity.Pid};kind={identity.Kind};generation={identity.Generation};rawCT={created};architecture={architecture};ready=True");
        return child;
    }
    private static void CheckOriginal(OwnedChild child)
    {
        Require(WaitForSingleObject(child.Handle, 0) == 258 && GetProcessTimes(child.Handle, out ulong created, out _, out _, out _)
            && created == child.Target.Created100ns, "Original cmd fixture handle is no longer alive or has a different raw FILETIME.");
        var identity = NativeMonitor.QueryProcessIdentity(child.Handle, child.Target.Key.Kind);
        Require(identity.State == SampleState.Valid && identity.Boot == child.Target.Key.Boot && identity.Pid == child.Target.Key.Pid
            && identity.Kind == child.Target.Key.Kind && identity.Generation == child.Target.Key.Generation, "Original cmd fixture full key changed.");
        Require(IsWow64Process2(child.Handle, out ushort machine, out ushort native), "Independent child bitness query failed.");
        Require(native == 0x8664 && machine == (child.Architecture == ProcessArchitecture.X86 ? 0x14c : 0), "Own fixture is not the requested x64/WOW64 architecture.");
    }
    private static async Task<bool> WaitIdleAsync(ProcessDumpClient client, Action<string> log)
    {
        long began = Stopwatch.GetTimestamp(); bool confirmedPromptly = true;
        while (client.IsBusy) {
            if (confirmedPromptly && Stopwatch.GetElapsedTime(began) > TimeSpan.FromSeconds(5)) {
                confirmedPromptly = false;
                log("FAIL dump-qa worker cleanup still pending;keeping real parent alive until production lease is released.");
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        return confirmedPromptly;
    }
    private static async Task CheckNoOwnedJournalsAsync(string directory, CancellationToken cancellation)
    {
        using var io = await DumpRecoveryIo.EnterAsync(cancellation).ConfigureAwait(false);
        string prefix = "\\\\?\\" + Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        var records = DumpRecoveryStore.ReadAll(cancellation);
        Require(!records.Any(item => item.Record?.Destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true),
            "An own capture journal remains; no successful cleanup is claimed.");
        Require(!Directory.EnumerateFileSystemEntries(directory, ".nks-dump-*.tmp", SearchOption.TopDirectoryOnly).Any(),
            "An own incomplete temporary file remains; it is preserved for inspection.");
    }
    private static void CheckOrdinaryToken()
    {
        using var identity = WindowsIdentity.GetCurrent(); using var process = Process.GetCurrentProcess();
        Require(!identity.IsSystem && process.SessionId > 0, "Dump QA requires an ordinary interactive caller.");
        if (!OpenProcessToken(process.SafeHandle, 8, out var token)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using (token) {
            Require(GetTokenInformation(token, 20, out uint elevation, 4, out _) && elevation == 0,
                "Dump QA refuses an elevated caller; this check proves ordinary-user capture.");
        }
    }
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits {
        public long ProcessTime, JobTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits {
        public BasicLimits Basic; public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)] private static partial SafeFileHandle CreateJobObject(nint attributes, nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetInformationJobObject(SafeFileHandle job, int info, in JobLimits limits, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetProcessTimes(SafeProcessHandle process, out ulong created, out ulong ended, out ulong kernel, out ulong user);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWow64Process2(SafeProcessHandle process, out ushort machine, out ushort nativeMachine);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeFileHandle token);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetTokenInformation(SafeFileHandle token, int type, out uint value, uint length, out uint used);
}
