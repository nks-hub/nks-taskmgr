using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal enum ProcessActionCommand : ushort { Query, CloseWindows, Terminate, Suspend, Resume, Priority, Affinity, TerminateTree, QueryCpuSets, CpuSets, CloseHandle }
internal enum ProcessActionFrame : uint { Accepted, Results, Rejected, Failed, TreePlan, TreeResults, CpuSets }
internal enum ProcessActionOutcome : uint {
    Inspected, Applied, CloseRequested, TerminationRequested, NativeRequestAccepted, Refused,
    IdentityChanged, AlreadyExited, Error, Unsupported, Unverified, NotPerformed
}
internal enum ProcessActionReason : uint {
    None, InvalidTarget, OwnOrParent, SystemTarget, Critical, Protected, OwnerMismatch, AccessDenied,
    IdentityUnverifiable, RawTimeMismatch, NoWindows, WindowMismatch, InvalidArgument, MultipleGroups,
    PartialClose, ReadbackMismatch, ProtectionUnverifiable, ParentUnavailable, AlreadySame, WindowLimit, NativeUnavailable,
    TreeBoundary, TreeBudget, TreeCensus, TreeUncertain, HandleMissing, HandleChanged, HandleUnverifiable, HandleProtected
}
[Flags]
internal enum ProcessActionFlags : uint {
    IdentityVerified = 1, AliveBefore = 2, AliveAfter = 4, ExitedAfter = 8,
    PriorityAvailable = 16, AffinityAvailable = 32, EffectConfirmed = 64, MayHaveApplied = 128, ObjectMatched = 256
}
// Name null leaves the object name unchecked; an empty name requires an unnamed object.
internal sealed record ProcessHandleSpec(ulong Value, uint GrantedAccess, uint TypeIndex, string Type, string? Name, ProcessHandleFingerprint Fingerprint);
internal sealed record ProcessActionTarget(ProcessKey Key, ulong Created100ns, ulong Window = 0);
internal sealed record ProcessActionsRequest(ProcessActionCommand Command, ProcessActionTarget[] Targets, ulong Argument = 0, uint Group = 0,
    ProcessCpuSetsSelection? CpuSets = null, ProcessHandleSpec? Handle = null);
internal sealed record ProcessActionResult(ProcessActionTarget Target, ulong ObservedCreated100ns, ProcessActionOutcome Outcome,
    ProcessActionReason Reason, uint Error, int NtStatus, ProcessActionFlags Flags, uint PriorityClass,
    ulong ProcessMask, ulong SystemMask, uint Group, uint PriorityError, uint AffinityError, uint WindowsPosted, uint WindowsFailed);
internal sealed record ProcessActionsBatch(ProcessActionCommand Command, ProcessActionFrame Frame, uint Error,
    ProcessActionResult[] Results, DateTimeOffset ReceivedAt)
{
    internal bool Accepted { get; init; }
    internal ProcessTreeActionsPlan? Tree { get; init; }
    internal ProcessCpuSetsSnapshot? CpuSets { get; init; }
}

internal static partial class ProcessActionsProtocol
{
    private const uint Magic = 0x31414b4e;
    internal const int ResponseHeaderBytes = 64;

    internal static byte[] Request(ProcessActionsRequest request)
    {
        if (request.Command is ProcessActionCommand.QueryCpuSets or ProcessActionCommand.CpuSets) return CpuSetsRequest(request);
        if (request.Command == ProcessActionCommand.CloseHandle) return CloseHandleRequest(request);
        if (request.CpuSets is not null) throw new ArgumentException("Výběr CPU sets nepatří k této akci.", nameof(request));
        if (request.Handle is not null) throw new ArgumentException("Handle nepatří k této akci.", nameof(request));
        if ((ushort)request.Command > (ushort)ProcessActionCommand.TerminateTree || request.Targets.Length is < 1 or > 64)
            throw new ArgumentException("Akce vyžaduje 1 až 64 vybraných procesů.", nameof(request));
        if (request.Command == ProcessActionCommand.TerminateTree && request.Targets.Length != 1)
            throw new ArgumentException("Ukončení stromu vyžaduje jeden potvrzený kořen.", nameof(request));
        if (request.Command == ProcessActionCommand.Affinity) {
            if (request.Targets.Length != 1 || request.Argument == 0 || request.Group > ushort.MaxValue)
                throw new ArgumentException("Afinita vyžaduje jeden proces, neprázdnou masku a jednu skupinu procesorů.", nameof(request));
        } else if (request.Group != 0 || (request.Command == ProcessActionCommand.Priority
            ? !PriorityAllowed(request.Argument) : request.Argument != 0)) throw new ArgumentException("Neplatná hodnota akce.", nameof(request));
        var bytes = new byte[64 + 48 * request.Targets.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)request.Command);
        Write(bytes, 8, (uint)(bytes.Length - 64)); Write(bytes, 12, (uint)request.Targets.Length);
        RandomNumberGenerator.Fill(bytes.AsSpan(16, 24));
        if (ULong(bytes, 16) == 0) Write(bytes, 16, 1ul);
        if (ULong(bytes, 24) == 0 && ULong(bytes, 32) == 0) Write(bytes, 24, 1ul);
        Write(bytes, 40, request.Argument); Write(bytes, 48, request.Group);
        var keys = new HashSet<ProcessKey>();
        for (int i = 0; i < request.Targets.Length; ++i) {
            var target = request.Targets[i]; var key = target.Key; int at = 64 + i * 48;
            if (key.Boot.IsEmpty || key.Pid == 0 || key.Kind is not (GenerationKind.CreationTime or GenerationKind.SequenceNumber)
                || key.Generation == 0 || target.Created100ns == 0 || !keys.Add(key)
                || request.Command != ProcessActionCommand.CloseWindows && target.Window != 0)
                throw new ArgumentException("Výběr obsahuje proces bez ověřitelné identity.", nameof(request));
            Write(bytes, at, key.Boot.Low); Write(bytes, at + 8, key.Boot.High); Write(bytes, at + 16, key.Pid);
            Write(bytes, at + 20, (uint)key.Kind); Write(bytes, at + 24, key.Generation);
            Write(bytes, at + 32, target.Created100ns); Write(bytes, at + 40, target.Window);
        }
        return bytes;
    }

    internal static int PayloadLength(ReadOnlySpan<byte> header, ReadOnlySpan<byte> request)
    {
        if (header.Length != 64 || request.Length < 112 || UInt(header, 0) != Magic || !header[..8].SequenceEqual(request[..8])
            || !header.Slice(16, 24).SequenceEqual(request.Slice(16, 24)) || ULong(header, 48) != 0 || ULong(header, 56) != 0)
            throw Invalid("Pracovník vrátil jiný požadavek nebo neplatnou hlavičku.");
        uint bytes = UInt(header, 8), count = UInt(header, 12); var frame = Frame(header);
        if (BinaryPrimitives.ReadUInt16LittleEndian(request[6..]) is (ushort)ProcessActionCommand.QueryCpuSets or (ushort)ProcessActionCommand.CpuSets)
            return CpuSetsPayloadLength(header, frame, bytes, count);
        if (BinaryPrimitives.ReadUInt16LittleEndian(request[6..]) == (ushort)ProcessActionCommand.TerminateTree)
            return ProcessTreeActionsProtocol.PayloadLength(header, frame, bytes, count);
        if ((uint)frame > (uint)ProcessActionFrame.Failed || bytes > 112 * 64
            || (frame is ProcessActionFrame.Accepted or ProcessActionFrame.Results
                ? count != UInt(request, 12) || UInt(header, 44) != 0 || bytes != (frame == ProcessActionFrame.Accepted ? 0 : count * 112)
                : count != 0 || bytes != 0)) throw Invalid("Pracovník vrátil neplatný typ nebo délku odpovědi.");
        return (int)bytes;
    }

    internal static ProcessActionFrame Frame(ReadOnlySpan<byte> header) => (ProcessActionFrame)UInt(header, 40);

    internal static ProcessActionsBatch Response(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> request)
    {
        if (PayloadLength(header, request) != payload.Length || Frame(header) == ProcessActionFrame.Accepted)
            throw Invalid("Chybí konečný výsledek požadavku.");
        var frame = Frame(header); var command = (ProcessActionCommand)BinaryPrimitives.ReadUInt16LittleEndian(request[6..]);
        var rows = frame is ProcessActionFrame.Results or ProcessActionFrame.CpuSets ? new ProcessActionResult[UInt(header, 12)] : [];
        for (int i = 0; i < rows.Length; ++i) {
            var row = payload.Slice(i * 112, 112); var expected = request.Slice(64 + i * 48, 48);
            var outcome = (ProcessActionOutcome)UInt(row, 48); var reason = (ProcessActionReason)UInt(row, 52);
            var flags = (ProcessActionFlags)UInt(row, 64);
            if (!row[..40].SequenceEqual(expected[..40]) || UInt(row, 108) != 0 || (uint)outcome > (uint)ProcessActionOutcome.Unverified
                || (uint)reason > (uint)ProcessActionReason.NativeUnavailable && !(command == ProcessActionCommand.CloseHandle
                    && reason is >= ProcessActionReason.HandleMissing and <= ProcessActionReason.HandleProtected)
                || ((uint)flags & ~(command == ProcessActionCommand.CloseHandle ? 511u : 255u)) != 0
                || flags.HasFlag(ProcessActionFlags.ObjectMatched) && outcome != ProcessActionOutcome.Applied
                || flags.HasFlag(ProcessActionFlags.AliveAfter) && flags.HasFlag(ProcessActionFlags.ExitedAfter)
                || flags.HasFlag(ProcessActionFlags.IdentityVerified) && ULong(row, 40) != ULong(expected, 32)
                || flags.HasFlag(ProcessActionFlags.PriorityAvailable) && (UInt(row, 68) == 0 || UInt(row, 92) != 0)
                || flags.HasFlag(ProcessActionFlags.AffinityAvailable) && (ULong(row, 72) == 0 || ULong(row, 80) == 0
                    || (ULong(row, 72) & ~ULong(row, 80)) != 0 || UInt(row, 88) > ushort.MaxValue || UInt(row, 96) != 0)
                || !flags.HasFlag(ProcessActionFlags.PriorityAvailable) && UInt(row, 68) != 0
                || !flags.HasFlag(ProcessActionFlags.AffinityAvailable) && (ULong(row, 72) != 0 || ULong(row, 80) != 0 || UInt(row, 88) != 0)
                || outcome == ProcessActionOutcome.Inspected && command is not (ProcessActionCommand.Query or ProcessActionCommand.QueryCpuSets)
                || outcome == ProcessActionOutcome.CloseRequested && command != ProcessActionCommand.CloseWindows
                || outcome == ProcessActionOutcome.TerminationRequested && command != ProcessActionCommand.Terminate
                || outcome == ProcessActionOutcome.NativeRequestAccepted && command is not (ProcessActionCommand.Suspend or ProcessActionCommand.Resume)
                || outcome is ProcessActionOutcome.CloseRequested or ProcessActionOutcome.TerminationRequested or ProcessActionOutcome.NativeRequestAccepted
                    && (flags & (ProcessActionFlags.IdentityVerified | ProcessActionFlags.AliveBefore | ProcessActionFlags.MayHaveApplied))
                        != (ProcessActionFlags.IdentityVerified | ProcessActionFlags.AliveBefore | ProcessActionFlags.MayHaveApplied)
                || outcome == ProcessActionOutcome.Applied && (command is not (ProcessActionCommand.Priority or ProcessActionCommand.Affinity or ProcessActionCommand.Terminate or ProcessActionCommand.CpuSets or ProcessActionCommand.CloseHandle)
                    || (flags & (ProcessActionFlags.IdentityVerified | ProcessActionFlags.AliveBefore | ProcessActionFlags.EffectConfirmed | ProcessActionFlags.MayHaveApplied))
                        != (ProcessActionFlags.IdentityVerified | ProcessActionFlags.AliveBefore | ProcessActionFlags.EffectConfirmed | ProcessActionFlags.MayHaveApplied))
                || (command is ProcessActionCommand.Query or ProcessActionCommand.QueryCpuSets) && (flags & (ProcessActionFlags.MayHaveApplied | ProcessActionFlags.EffectConfirmed)) != 0
                || outcome == ProcessActionOutcome.Inspected && (flags & (ProcessActionFlags.IdentityVerified | ProcessActionFlags.AliveBefore))
                    != (ProcessActionFlags.IdentityVerified | ProcessActionFlags.AliveBefore)
                || outcome == ProcessActionOutcome.CloseRequested && UInt(row, 100) == 0
                || outcome == ProcessActionOutcome.Applied && command == ProcessActionCommand.Terminate && !flags.HasFlag(ProcessActionFlags.ExitedAfter)
                || flags.HasFlag(ProcessActionFlags.EffectConfirmed) && outcome != ProcessActionOutcome.Applied
                || command != ProcessActionCommand.CloseWindows && (UInt(row, 100) != 0 || UInt(row, 104) != 0))
                throw Invalid("Výsledek nepatří vybranému procesu nebo obsahuje neplatná pole.");
            var key = new ProcessKey(new(ULong(row, 0), ULong(row, 8)), UInt(row, 16), (GenerationKind)UInt(row, 20), ULong(row, 24));
            var target = new ProcessActionTarget(key, ULong(row, 32), ULong(expected, 40));
            rows[i] = new(target, ULong(row, 40), outcome, reason, UInt(row, 56), unchecked((int)UInt(row, 60)), flags,
                UInt(row, 68), ULong(row, 72), ULong(row, 80), UInt(row, 88), UInt(row, 92), UInt(row, 96), UInt(row, 100), UInt(row, 104));
        }
        return new(command, frame, UInt(header, 44), rows, DateTimeOffset.UtcNow) {
            CpuSets = frame == ProcessActionFrame.CpuSets ? CpuSetsSnapshot(payload[112..], rows[0], request) : null
        };
    }

    private static byte[] CloseHandleRequest(ProcessActionsRequest request)
    {
        var handle = request.Handle;
        if (request.Targets.Length != 1 || request.Argument != 0 || request.Group != 0 || request.CpuSets is not null || handle is null
            || handle.Value == 0 || (handle.Value & 3) != 0 || handle.Value >= 0xfffffff0 || handle.Type.Length is < 1 or > 128
            || handle.Name is { Length: > 32767 } || handle.Type.Contains('\0') || handle.Name?.Contains('\0') == true
            || !ProcessDetailsProtocol.FingerprintValid(handle.Fingerprint, handle.Type))
            throw new ArgumentException("Zavření handle vyžaduje jeden proces, platnou hodnotu a ověřený typ objektu.", nameof(request));
        var basis = Request(request with { Command = ProcessActionCommand.Query, Handle = null });
        string text = handle.Type + handle.Name;
        var bytes = new byte[112 + 48 + text.Length * 2];
        basis.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)ProcessActionCommand.CloseHandle);
        Write(bytes, 8, (uint)(bytes.Length - 64)); Write(bytes, 40, handle.Value); Write(bytes, 48, handle.Name is null ? 0u : 1u);
        Write(bytes, 112, handle.GrantedAccess); Write(bytes, 116, handle.TypeIndex);
        Write(bytes, 120, (uint)handle.Type.Length); Write(bytes, 124, (uint)(handle.Name?.Length ?? 0));
        var print = handle.Fingerprint;
        Write(bytes, 128, print.Kind); Write(bytes, 132, print.Id); Write(bytes, 136, print.Value0); Write(bytes, 144, print.Value1); Write(bytes, 152, print.Value2);
        MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes.AsSpan(160));
        return bytes;
    }

    private static bool PriorityAllowed(ulong value) => value is 0x40 or 0x4000 or 0x20 or 0x8000 or 0x80;
    private static uint UInt(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
    private static ulong ULong(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt64LittleEndian(data[at..]);
    private static void Write(Span<byte> data, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data[at..], value);
    private static void Write(Span<byte> data, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data[at..], value);
    private static InvalidDataException Invalid(string text) => new(text);
}
