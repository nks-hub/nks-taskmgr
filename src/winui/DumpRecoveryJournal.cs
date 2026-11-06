using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json.Serialization;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal enum DumpRecoveryPhase { Intent, TempOpened, StartingHelper, WorkerReady, Ready }
internal sealed record DumpCreator(BootIdentifier Boot, uint Pid, ulong Created100ns);
internal sealed record DumpRecoveryRecord(int Version, string Id, string OwnerSid, string FileOwnerSid, DateTimeOffset Started,
    DumpRecoveryPhase Phase, DumpCreator Creator, DumpCreator? Helper, ProcessDumpTarget Target, ProcessDumpMode Mode,
    string Destination, string Temporary, DumpFileIdentity ParentId, DumpFileIdentity FileId,
    ulong RequestId, ulong NonceLow, ulong NonceHigh, ulong FileBytes, string Sha256, ProcessArchitecture Architecture, uint CaptureFlags);

[JsonSourceGenerationOptions(MaxDepth = 12, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DumpRecoveryRecord))]
internal partial class DumpRecoveryJsonContext : JsonSerializerContext { }

internal sealed class DumpRecoveryJournal
{
    internal DumpRecoveryRecord Record { get; private set; }
    internal string Proof { get; private set; }
    private bool removed;
    private DumpRecoveryJournal(DumpRecoveryRecord record, string proof) { Record = record; Proof = proof; }
    internal static DumpRecoveryJournal Create(string id, ProcessDumpTarget target, ProcessDumpMode mode, string destination, string temporary, DumpFileIdentity parent)
    {
        var value = new DumpRecoveryRecord(1, id, DumpRecoveryProcesses.UserSid, "", DateTimeOffset.UtcNow, DumpRecoveryPhase.Intent,
            DumpRecoveryProcesses.Current(), null, target, mode, destination, temporary, parent, default, 0, 0, 0, 0, "", ProcessArchitecture.Unknown, 0);
        return new(value, Persist(() => DumpRecoveryStore.Write(value, null)));
    }
    internal static DumpRecoveryJournal Attach(DumpRecoveryRecord record, string proof) => new(record, proof);
    private void Save(DumpRecoveryRecord next) { string proof = Persist(() => DumpRecoveryStore.Write(next, Proof)); Record = next; Proof = proof; }
    internal void TempOpened(DumpFileIdentity file, string owner) => Save(Record with { Phase = DumpRecoveryPhase.TempOpened, FileId = file, FileOwnerSid = owner });
    internal void StartingHelper() => Save(Record with { Phase = DumpRecoveryPhase.StartingHelper });
    internal void WorkerReady(Process helper, ReadOnlySpan<byte> request) => Save(Record with {
        Phase = DumpRecoveryPhase.WorkerReady, Helper = DumpRecoveryProcesses.Identity(helper.SafeHandle),
        RequestId = BinaryPrimitives.ReadUInt64LittleEndian(request[16..]), NonceLow = BinaryPrimitives.ReadUInt64LittleEndian(request[24..]), NonceHigh = BinaryPrimitives.ReadUInt64LittleEndian(request[32..])
    });
    internal void Ready(ProcessDumpResult result)
    {
        if (Record.Phase == DumpRecoveryPhase.Ready && Record.Sha256 == result.Sha256 && Record.FileBytes == result.FileBytes) return;
        Save(Record with { Phase = DumpRecoveryPhase.Ready, Sha256 = result.Sha256, FileBytes = result.FileBytes, Architecture = result.Architecture, CaptureFlags = result.CaptureFlags });
    }
    internal void Remove() { if (removed) return; Persist(() => { DumpRecoveryStore.Remove(Record.Id, Proof); return true; }); removed = true; }
    private static T Persist<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception error) when (error is System.Text.Json.JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) {
            throw new IOException(Loc.T("Záznam obnovy nelze bezpečně zapsat nebo odstranit. Existující záznam zůstane zachovaný."), error);
        }
    }
}
