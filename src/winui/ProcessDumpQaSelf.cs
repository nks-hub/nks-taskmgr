using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static partial class ProcessDumpQaChecks
{
    private static async Task CheckOwnGuiMiniAsync(ProcessDumpClient client, string directory, DumpCreator creator,
        Func<CancellationToken, Task> uiProbe, Action<string> log, CancellationToken cancellation)
    {
        using var gui = Process.GetCurrentProcess();
        SafeProcessHandle handle = gui.SafeHandle;
        if (!GetProcessTimes(handle, out ulong created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Read the held GUI's own raw creation time");
        var identity = NativeMonitor.QueryProcessIdentity(handle, GenerationKind.SequenceNumber);
        if (identity.State is SampleState.Unsupported or SampleState.Unavailable)
            identity = NativeMonitor.QueryProcessIdentity(handle, GenerationKind.CreationTime);
        Require(identity.State == SampleState.Valid && identity.Pid == (uint)gui.Id && identity.Pid == (uint)Environment.ProcessId
            && identity.Boot == creator.Boot && !identity.Boot.IsEmpty && identity.Generation != 0 && created != 0
            && creator.Pid == identity.Pid && creator.Created100ns == created
            && identity.Kind is GenerationKind.CreationTime or GenerationKind.SequenceNumber
            && (identity.Kind != GenerationKind.CreationTime || identity.Generation == created), "The original GUI full identity is unavailable.");
        var target = new ProcessDumpTarget(new(identity.Boot, identity.Pid, identity.Kind, identity.Generation), created);
        string executable = Environment.ProcessPath ?? throw new IOException("The actual GUI image is unavailable.");
        string imageName = HeldGuiImageName(handle, executable);
        string destination = Path.Combine(directory, "own-gui-mini.dmp");
        CheckOwnGuiIdentity(handle, target);
        await uiProbe(cancellation).ConfigureAwait(false);
        CheckOwnGuiIdentity(handle, target);
        log($"dump-self begin;boot={identity.Boot.High:X16}{identity.Boot.Low:X16};pid={identity.Pid};kind={identity.Kind};generation={identity.Generation};rawCT={created};image={imageName};architecture=X64;mode=Mini;uiDispatchBefore=True;targetInChildJob=False");
        var result = await client.CaptureAsync(target, ProcessDumpMode.Mini, destination, cancellation).ConfigureAwait(false);
        Require(await WaitIdleAsync(client, log).ConfigureAwait(false), "Own-GUI dump cleanup exceeded the existing 5 s check.");
        log($"dump-self result;state={result.State};error={result.Error};flags=0x{result.CaptureFlags:X};architecture={result.Architecture};bytes={result.FileBytes};observedCT={result.ObservedCreated100ns};saved={result.SavedPath is not null};clientBusy={client.IsBusy};cleanupError={client.CleanupError}");
        Require(result.State == ProcessDumpState.Captured && result.Error == 0 && result.SavedPath is not null
            && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(result.SavedPath), Path.GetFullPath(destination))
            && result.ObservedCreated100ns == created && result.Architecture == ProcessArchitecture.X64 && result.CaptureFlags == 0
            && !client.IsBusy && result.RecoveryWarning is null && client.CleanupError is null,
            "The real production worker did not confirm and publish the exact own-GUI Mini dump.");
        var inspected = ProcessDumpQaReader.Inspect(destination, target, ProcessDumpMode.Mini, ProcessArchitecture.X64,
            result, cancellation, imageName);
        Require(inspected.ProcessIdPresent && inspected.ProcessCreationTimePresent,
            "The actual own-GUI dump lacks a verified process identifier or creation-time stream field.");
        CheckOwnGuiIdentity(handle, target);
        await CheckNoOwnedJournalsAsync(directory, cancellation).ConfigureAwait(false);
        await uiProbe(cancellation).ConfigureAwait(false);
        CheckOwnGuiIdentity(handle, target);
        cancellation.ThrowIfCancellationRequested();
        log($"dump-self captured;pid={target.Key.Pid};kind={target.Key.Kind};generation={target.Key.Generation};rawCT={created};architecture=X64;mode=Mini;bytes={inspected.Bytes};sha256={inspected.Sha256};fileId={result.FileId};threads={inspected.Threads};modules={inspected.Modules};memoryRanges={inspected.MemoryRanges};processIdInDump={inspected.ProcessIdPresent};processCreationTimeInDump={inspected.ProcessCreationTimePresent};streams={inspected.Streams};workerRetired=True;retirementSource=productionClientVerifiedExit+idle;clientIdle=True;ownedTemporary=False;ownedJournal=False;guiStillOriginal=True;uiDispatchAfter=True;file={destination}");
    }

    private static string HeldGuiImageName(SafeProcessHandle handle, string executable)
    {
        var text = new char[32768]; uint length = (uint)text.Length;
        unsafe { fixed (char* buffer = text) if (!QaQueryFullProcessImageName(handle, 0, buffer, ref length)) throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        string actual = new(text, 0, checked((int)length));
        Require(StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(actual), Path.GetFullPath(executable)),
            "The held GUI image differs from the actual current executable.");
        string name = Path.GetFileName(executable);
        Require(name.Length > 0 && StringComparer.OrdinalIgnoreCase.Equals(name, Path.GetFileName(actual)), "The held GUI image lacks a matching physical basename.");
        return name;
    }

    private static void CheckOwnGuiIdentity(SafeProcessHandle handle, ProcessDumpTarget target)
    {
        Require(target.Key.Pid == (uint)Environment.ProcessId && WaitForSingleObject(handle, 0) == 258
            && GetProcessTimes(handle, out ulong created, out _, out _, out _) && created == target.Created100ns,
            "The original GUI held handle is not the same live process/raw creation time.");
        var actual = NativeMonitor.QueryProcessIdentity(handle, target.Key.Kind);
        Require(actual.State == SampleState.Valid && actual.Boot == target.Key.Boot && actual.Pid == target.Key.Pid
            && actual.Kind == target.Key.Kind && actual.Generation == target.Key.Generation, "The original GUI lifetime key changed.");
        Require(IsWow64Process2(handle, out ushort machine, out ushort native) && machine == 0 && native == 0x8664,
            "The actual GUI dump target is not independently verified x64.");
    }
}
