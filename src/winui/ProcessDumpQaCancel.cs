using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static partial class ProcessDumpQaChecks
{
    private const int CancelAllocationBytes = 256 * 1024 * 1024;
    private const long CancelObservationLimit = 64 * 1024 * 1024;
    private sealed record CancelWitness(DumpRecoveryRecord Record, SafeProcessHandle Worker, SafeFileHandle Temporary,
        long FirstBytes, long LastBytes) : IDisposable
    {
        public void Dispose() { Temporary.Dispose(); Worker.Dispose(); }
    }

    private static async Task CheckInFlightCancelAsync(OwnedChild child, ProcessDumpClient client, string directory,
        ProcessDumpFile directoryLease, DumpCreator creator, Func<CancellationToken, Task> uiProbe,
        Action<string> log, CancellationToken cancellation)
    {
        nint allocation = 0;
        Task<ProcessDumpResult>? capture = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        string destination = Path.Combine(directory, "x64-inflight-cancel.dmp");
        try {
            CheckOriginal(child);
            Require(child.Architecture == ProcessArchitecture.X64, "In-flight cancellation requires the original x64 child.");
            allocation = QaVirtualAllocEx(child.Handle, 0, (nuint)CancelAllocationBytes, 0x3000, 4);
            if (allocation == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Allocate only the owned child's capture memory");
            FillCancelAllocation(child, allocation, cancellation);
            CheckOriginal(child);
            log($"dump-qa inflight-memory;boot={creator.Boot.High:X16}{creator.Boot.Low:X16};pid={child.Target.Key.Pid};kind={child.Target.Key.Kind};generation={child.Target.Key.Generation};rawCT={child.Target.Created100ns};committedBytes={CancelAllocationBytes};writtenBytes={CancelAllocationBytes};executable=False");
            capture = client.CaptureAsync(child.Target, ProcessDumpMode.Full, destination, stop.Token);
            using var witness = await ObserveCaptureAsync(child, creator, directory, directoryLease, destination, client, capture, log, cancellation).ConfigureAwait(false);
            var helper = witness.Record.Helper ?? throw new IOException("The observed capture lacks a full worker identity.");
            string refusedPath = Path.Combine(directory, "inflight-busy-refused.dmp");
            bool busyRefused = false;
            _ = RequireInFlight(witness, child, creator, client, capture);
            try { _ = await client.CaptureAsync(child.Target, ProcessDumpMode.Full, refusedPath, cancellation).ConfigureAwait(false); }
            catch (IOException error) when (error.Message == Loc.T("Předchozí dump nebo jeho úklid ještě probíhá.")) { busyRefused = true; }
            Require(busyRefused && !File.Exists(refusedPath), "The active client accepted another capture or created its destination.");
            var ownedTemporaries = Directory.EnumerateFiles(directory, ".nks-dump-*.tmp", SearchOption.TopDirectoryOnly).Take(2).ToArray();
            Require(ownedTemporaries.Length == 1 && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(ownedTemporaries[0]), Path.GetFileName(witness.Record.Temporary)),
                "The busy-rejected request created a second temporary file or the witnessed capture ended already.");
            await uiProbe(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            long cancelBytes = RequireInFlight(witness, child, creator, client, capture);
            Require(cancelBytes >= witness.LastBytes, "The owned capture file shrank before cancellation.");
            witness.Temporary.Dispose();
            log($"dump-qa inflight-cancel-request;workerPid={helper.Pid};workerRawCT={helper.Created100ns};firstBytes={witness.FirstBytes};grownBytes={witness.LastBytes};cancelBytes={cancelBytes};captureCompleted=False;clientBusy=True;workerAlive=True;busyRefused=True;uiDispatch=True");
            stop.Cancel();
            bool canceled = false;
            try { _ = await capture.ConfigureAwait(false); }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled && !cancellation.IsCancellationRequested, "The witnessed in-flight capture did not propagate its explicit cancellation.");
            bool busyAfterCancellation = client.IsBusy;
            uint workerAfterCancellation = WaitForSingleObject(witness.Worker, 0);
            Require(workerAfterCancellation is 0 or 258 && (busyAfterCancellation || workerAfterCancellation == 0),
                "The client released its busy lease while the original worker was still alive.");
            log($"dump-qa inflight-cancel-return;clientBusy={busyAfterCancellation};workerRetired={workerAfterCancellation == 0}");
            Require(await WaitIdleAsync(client, log).ConfigureAwait(false), "In-flight worker retirement exceeded the existing 5 s cleanup check.");
            Require(!client.IsBusy && WaitForSingleObject(witness.Worker, 0) == 0 && client.CleanupError is null,
                "The client became idle without confirmed worker retirement and cleanup.");
            Require(!File.Exists(destination) && !File.Exists(refusedPath), "In-flight cancellation falsely published a final dump.");
            await CheckNoOwnedJournalsAsync(directory, cancellation).ConfigureAwait(false);
            CheckOriginal(child);
            log("dump-qa inflight-cancel=True;workerRetired=True;clientIdle=True;cleanupError=False;finalFile=False;ownedTemporary=False;ownedJournal=False;targetStillOriginal=True");
        } finally {
            stop.Cancel();
            try {
                if (capture is not null) {
                    try {
                        var result = await capture.ConfigureAwait(false);
                        log($"dump-qa inflight-final-observation;state={result.State};saved={result.SavedPath is not null}");
                    } catch (Exception error) { log("dump-qa inflight-final-observation;exception=" + error); }
                    Require(await WaitIdleAsync(client, log).ConfigureAwait(false), "In-flight cleanup was not confirmed within the existing 5 s check.");
                }
            } finally {
                if (allocation != 0) {
                    uint life = WaitForSingleObject(child.Handle, 0);
                    Require(life is 0 or 258, "The owned child's lifetime is unavailable during allocation cleanup.");
                    if (life == 258 && !QaVirtualFreeEx(child.Handle, allocation, 0, 0x8000))
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Release only the owned child's capture allocation");
                }
            }
        }
    }

    private static unsafe void FillCancelAllocation(OwnedChild child, nint allocation, CancellationToken cancellation)
    {
        var bytes = new byte[64 * 1024]; Array.Fill(bytes, (byte)0xa5);
        fixed (byte* source = bytes) {
            for (int offset = 0; offset < CancelAllocationBytes; offset += bytes.Length) {
                cancellation.ThrowIfCancellationRequested();
                if (!QaWriteProcessMemory(child.Handle, allocation + offset, source, (nuint)bytes.Length, out nuint written))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Write only the owned child's nonexecutable capture memory");
                Require(written == (nuint)bytes.Length, "The owned capture allocation was not fully written.");
            }
        }
    }

    private static async Task<CancelWitness> ObserveCaptureAsync(OwnedChild child, DumpCreator creator, string directory,
        ProcessDumpFile directoryLease, string destination, ProcessDumpClient client, Task<ProcessDumpResult> capture,
        Action<string> log, CancellationToken cancellation)
    {
        string recoveryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NksTaskMgr", "DumpRecovery");
        using var recoveryDirectory = ProcessDumpFile.HoldDirectory(recoveryPath);
        CancelWitness? witness = null;
        long previous = 0;
        try {
            for (;;) {
                cancellation.ThrowIfCancellationRequested();
                if (capture.IsCompleted) throw new InvalidOperationException("In-flight coverage not observed: capture completed before the required growing-file witness; no retry.");
                if (witness is null) {
                    var temporaryNames = Directory.EnumerateFiles(directory, ".nks-dump-*.tmp", SearchOption.TopDirectoryOnly).Take(2).ToArray();
                    Require(temporaryNames.Length <= 1, "More than one temporary capture exists in the private run directory.");
                    if (temporaryNames.Length == 1) {
                        string name = Path.GetFileName(temporaryNames[0]);
                        Require(name.Length == 46 && Guid.TryParseExact(name.AsSpan(10, 32), "N", out _), "Unexpected owned temporary filename.");
                        var record = ReadOwnCancelJournal(recoveryDirectory, name.Substring(10, 32));
                        if (record is not null && record.Phase >= DumpRecoveryPhase.WorkerReady) {
                            Require(record.Phase == DumpRecoveryPhase.WorkerReady && record.Creator == creator && record.Target == child.Target
                                && record.Mode == ProcessDumpMode.Full && StringComparer.OrdinalIgnoreCase.Equals(record.Destination, "\\\\?\\" + Path.GetFullPath(destination))
                                && StringComparer.OrdinalIgnoreCase.Equals(record.Temporary, "\\\\?\\" + Path.GetFullPath(temporaryNames[0]))
                                && record.ParentId == ProcessDumpFile.FileIdentity(directoryLease.Parent), "Capture journal is ready already or does not bind the exact owned request.");
                            witness = OpenCancelWitness(record, directoryLease, name, creator);
                        }
                    }
                }
                if (witness is not null) {
                    long size = RequireInFlight(witness, child, creator, client, capture);
                    Require(size >= previous, "The observed capture EOF moved backwards.");
                    if (previous > 0 && size > previous) {
                        var helper = witness.Record.Helper ?? throw new IOException("The growing capture lacks its worker identity.");
                        log($"dump-qa inflight-observed;mode=Full;workerPid={helper.Pid};workerRawCT={helper.Created100ns};fileId={witness.Record.FileId};firstBytes={previous};grownBytes={size};clientBusy=True;captureCompleted=False");
                        var result = witness with { FirstBytes = previous, LastBytes = size }; witness = null; return result;
                    }
                    previous = size;
                }
                // Polling observes the real write; elapsed time never triggers cancellation.
                await Task.Delay(5, cancellation).ConfigureAwait(false);
            }
        } finally { witness?.Dispose(); }
    }

    private static DumpRecoveryRecord? ReadOwnCancelJournal(ProcessDumpFile directory, string id)
    {
        byte[] bytes;
        try {
            using var file = ProcessDumpFile.OpenEntry(directory.Parent, id + ".json", share: 7);
            ProcessDumpFile.CheckSingleFile(file);
            Require(ProcessDumpFile.Owner(file) == DumpRecoveryProcesses.UserSid, "Owned capture journal has a different owner.");
            long size = RandomAccess.GetLength(file);
            Require(size is > 0 and <= 512 * 1024, "Owned capture journal exceeds its bounded read limit.");
            bytes = new byte[checked((int)size)]; int offset = 0;
            while (offset < bytes.Length) {
                int read = RandomAccess.Read(file, bytes.AsSpan(offset), offset);
                Require(read > 0, "Owned capture journal ended before its declared length."); offset += read;
            }
        } catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3 or 32 or 33) { return null; }
        var record = JsonSerializer.Deserialize(bytes, DumpRecoveryJsonContext.Default.DumpRecoveryRecord)
            ?? throw new IOException("Owned capture journal is empty.");
        DumpRecoveryStore.Validate(record, id + ".json");
        return record;
    }

    private static CancelWitness OpenCancelWitness(DumpRecoveryRecord record, ProcessDumpFile directory, string temporaryName, DumpCreator creator)
    {
        var helper = record.Helper ?? throw new IOException("Owned capture journal lacks a full worker identity.");
        var worker = QaOpenProcess(0x101000, false, helper.Pid);
        SafeFileHandle? temporary = null;
        try {
            if (worker.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Open the journal-bound worker read-only");
            Require(GetProcessTimes(worker, out ulong created, out _, out _, out _) && created == helper.Created100ns,
                "The held worker has a different raw creation time.");
            RequireWorkerIdentity(worker, helper, creator);
            var chars = new char[32768]; uint length = (uint)chars.Length;
            unsafe { fixed (char* buffer = chars) if (!QaQueryFullProcessImageName(worker, 0, buffer, ref length)) throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            Require(string.Equals(new string(chars, 0, checked((int)length)), Path.Combine(AppContext.BaseDirectory, "nks-dump-worker.exe"), StringComparison.OrdinalIgnoreCase),
                "The held worker image is not the actual bundled x64 dump helper.");
            temporary = ProcessDumpFile.OpenEntry(directory.Parent, temporaryName, 0x120080, 7);
            ProcessDumpFile.CheckSingleFile(temporary);
            Require(ProcessDumpFile.FileIdentity(temporary) == record.FileId && ProcessDumpFile.Owner(temporary) == record.FileOwnerSid,
                "Observed temporary file does not match the journal's original file identity/owner.");
            return new(record, worker, temporary, 0, 0);
        } catch { temporary?.Dispose(); worker.Dispose(); throw; }
    }

    private static long RequireInFlight(CancelWitness witness, OwnedChild child, DumpCreator creator, ProcessDumpClient client, Task<ProcessDumpResult> capture)
    {
        Require(!capture.IsCompleted && client.IsBusy, "In-flight coverage not observed: capture completed or client became idle before cancellation.");
        RequireWorkerIdentity(witness.Worker, witness.Record.Helper!, creator);
        CheckOriginal(child);
        if (!QaFileStandardInfo(witness.Temporary, 1, out var information, 24)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Read capture EOF without data access");
        Require(information.EndOfFile is >= 0 and < CancelObservationLimit, "In-flight coverage not observed below the 64 MiB write boundary; no retry.");
        return information.EndOfFile;
    }

    private static void RequireWorkerIdentity(SafeProcessHandle worker, DumpCreator helper, DumpCreator creator)
    {
        var identity = NativeMonitor.QueryProcessIdentity(worker, GenerationKind.CreationTime);
        Require(identity.State == SampleState.Valid && identity.Boot == creator.Boot && identity.Boot == helper.Boot
            && identity.Pid == helper.Pid && identity.Kind == GenerationKind.CreationTime && identity.Generation == helper.Created100ns
            && WaitForSingleObject(worker, 0) == 258, "The held journal-bound worker is no longer the same live process.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct QaStandardInfo {
        public long AllocationSize, EndOfFile; public uint Links; public byte DeletePending, Directory;
    }
    [LibraryImport("kernel32.dll", EntryPoint = "VirtualAllocEx", SetLastError = true)]
    private static partial nint QaVirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint type, uint protection);
    [LibraryImport("kernel32.dll", EntryPoint = "VirtualFreeEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool QaVirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint type);
    [LibraryImport("kernel32.dll", EntryPoint = "WriteProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static unsafe partial bool QaWriteProcessMemory(SafeProcessHandle process, nint address, void* data, nuint size, out nuint written);
    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial SafeProcessHandle QaOpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static unsafe partial bool QaQueryFullProcessImageName(SafeProcessHandle process, uint flags, char* text, ref uint length);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool QaFileStandardInfo(SafeFileHandle file, int kind, out QaStandardInfo information, uint length);
}
