using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NksTaskMgr.WinUI;

internal enum ServiceAction : uint { Start = 1, Stop = 2, Restart = 3 }
internal enum ServiceActionPhase : uint { Request, Plan, Accepted, Progress, Result, Error }
internal enum ServiceActionStatus : uint { Ok, AccessDenied, Unsupported, Changed, Timeout, InvalidRequest, LimitExceeded, Error, Cancelled, Partial }
internal enum ServiceActionOutcome : uint { NotAttempted, AlreadyDesired, Confirmed, Error, Changed, Timeout, Unknown, Cancelled }
[Flags] internal enum ServicePlanFlags : uint { Root = 1, StopRequired = 2, StartRequired = 4, InitiallyRunning = 8 }
[Flags] internal enum ServiceResultFlags : uint { StopRequested = 1, StopConfirmed = 2, StartRequested = 4, StartConfirmed = 8, ConfigVerified = 16, StateKnown = 32, MayHaveApplied = 64 }
internal sealed record ServiceActionNode(uint Id, ServicePlanFlags Flags, uint OriginalState, uint DesiredState, uint StopOrder, uint StartOrder,
    string Name, string Title, string Command, string Account, uint StartType, uint Type, string[] Dependencies);
internal sealed record ServiceActionPlan(ServiceAction Action, ulong RequestId, ulong NonceLow, ulong NonceHigh, ServiceActionNode[] Nodes);
internal sealed record ServiceActionItem(uint Id, ServiceActionOutcome Outcome, uint LastState, uint ObservationError, ServiceResultFlags Flags,
    uint StopError, uint StartError, uint Checkpoint, uint WaitHint, uint Win32Exit, uint ServiceExit, long ObservedAtNs);
internal sealed record ServiceActionsBatch(ServiceActionPlan? Plan, ServiceActionStatus Status, uint Error, ServiceActionItem[] Items,
    bool Completed, bool ApplySent, string Message = "");
internal readonly record struct ServiceActionHeader(ServiceActionPhase Phase, ServiceActionStatus Status, uint Error, int Payload, uint Count, ulong NonceLow, ulong NonceHigh);
internal sealed class ServiceActionException(ServiceActionStatus status, uint error) : IOException($"Správce služeb: {ServiceActionsProtocol.StatusText(status)}, Windows {error}.")
{
    internal ServiceActionStatus Status { get; } = status;
    internal uint Error { get; } = error;
}

internal static class ServiceActionsProtocol
{
    private const uint Magic = 0x31534b4e;
    internal const int HeaderBytes = 64, MaximumPayload = 4 * 1024 * 1024;
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);
    internal static string StatusText(ServiceActionStatus status) => status switch {
        ServiceActionStatus.Ok => "potvrzeno", ServiceActionStatus.AccessDenied => "odepřený přístup", ServiceActionStatus.Unsupported => "nepodporovaný stav",
        ServiceActionStatus.Changed => "plán se změnil", ServiceActionStatus.Timeout => "vypršel čas", ServiceActionStatus.InvalidRequest => "neplatný požadavek",
        ServiceActionStatus.LimitExceeded => "překročený limit", ServiceActionStatus.Cancelled => "zrušeno", ServiceActionStatus.Partial => "částečný výsledek", _ => "chyba"
    };
    internal static byte[] PlanRequest(ServiceAction action, string name)
    {
        if (action is < ServiceAction.Start or > ServiceAction.Restart || name.Length is < 1 or > 256 || name.Contains('\0')) throw new ArgumentException("Vyberte jednu službu.");
        var random = new byte[8]; RandomNumberGenerator.Fill(random);
        ulong id = (BinaryPrimitives.ReadUInt64LittleEndian(random) & (ulong)long.MaxValue) + 1;
        var bytes = Header(1, action, id, 0, 0, 4 + name.Length * 2, 0);
        Write(bytes, 64, (uint)name.Length);
        try { Utf16.GetBytes(name, bytes.AsSpan(68)); } catch (EncoderFallbackException error) { throw new ArgumentException("Neplatný název služby.", error); }
        return bytes;
    }
    internal static byte[] ApplyRequest(ServiceActionPlan plan, uint[] approved)
    {
        var expected = plan.Nodes.Where(row => row.Id != 0 && row.Flags.HasFlag(ServicePlanFlags.StopRequired)).Select(row => row.Id).ToHashSet();
        if (approved.Length > 63 || approved.Distinct().Count() != approved.Length || !expected.SetEquals(approved)) throw new ArgumentException("Schválený seznam závislých služeb neodpovídá plánu.");
        var bytes = Header(2, plan.Action, checked(plan.RequestId + 1), plan.NonceLow, plan.NonceHigh, 4 + approved.Length * 4, (uint)approved.Length);
        Write(bytes, 64, (uint)approved.Length);
        for (int i = 0; i < approved.Length; ++i) Write(bytes, 68 + i * 4, approved[i]);
        return bytes;
    }
    private static byte[] Header(ushort command, ServiceAction action, ulong id, ulong low, ulong high, int payload, uint count)
    {
        var bytes = new byte[HeaderBytes + payload]; Write(bytes, 0, Magic); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), command); Write(bytes, 8, (uint)payload); Write(bytes, 12, count);
        Write(bytes, 16, id); Write(bytes, 24, low); Write(bytes, 32, high); Write(bytes, 40, (uint)action); return bytes;
    }
    internal static ServiceActionHeader ReadHeader(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> request, ServiceActionPlan? plan)
    {
        if (bytes.Length != HeaderBytes || request.Length < HeaderBytes || UInt(bytes, 0) != Magic || !bytes[..8].SequenceEqual(request[..8])
            || ULong(bytes, 16) != ULong(request, 16) || UInt(bytes, 40) != UInt(request, 40) || ULong(bytes, 56) != 0)
            throw Invalid("Pracovník vrátil jiný nebo neplatný požadavek.");
        uint payload = UInt(bytes, 8), count = UInt(bytes, 12), phase = UInt(bytes, 44), status = UInt(bytes, 48);
        ulong low = ULong(bytes, 24), high = ULong(bytes, 32);
        if (payload > MaximumPayload || count > 64 || phase is < 1 or > 5 || status > 9
            || plan is not null && (low != plan.NonceLow || high != plan.NonceHigh)
            || phase == 1 && (plan is not null || count == 0 || status != 0 || (low == 0 && high == 0) || payload < count * 56)
            || phase == 2 && (plan is null || payload != 0 || count != plan.Nodes.Length || status != 0)
            || phase is 3 or 4 && (plan is null || count != plan.Nodes.Length || payload != count * 56)
            || phase == 3 && status != 0 || status == 0 && UInt(bytes, 52) != 0 || phase == 5 && (payload != 0 || count != 0 || status == 0)
            || plan is null && phase is not (1 or 5)) throw Invalid("Neplatná délka nebo fáze odpovědi služeb.");
        return new((ServiceActionPhase)phase, (ServiceActionStatus)status, UInt(bytes, 52), (int)payload, count, low, high);
    }
    internal static ServiceActionPlan DecodePlan(ServiceActionHeader header, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> request, string expectedName)
    {
        if (header.Phase == ServiceActionPhase.Error) throw new ServiceActionException(header.Status, header.Error);
        if (payload.Length != header.Payload) throw Invalid("Neúplný plán služeb.");
        var reader = new Reader(payload); var rows = new ServiceActionNode[header.Count]; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var action = (ServiceAction)UInt(request, 40);
        var stops = new HashSet<uint>(); var starts = new HashSet<uint>();
        for (uint i = 0; i < rows.Length; ++i) {
            var fixedRow = reader.Take(56); uint flags = UInt(fixedRow, 4), before = UInt(fixedRow, 8), desired = UInt(fixedRow, 12);
            uint stopOrder = UInt(fixedRow, 16), startOrder = UInt(fixedRow, 20), dependencies = UInt(fixedRow, 40);
            uint expectedFlags = (i == 0 ? 1u : 0u) | (before == 4 ? 8u : 0u)
                | (action != ServiceAction.Start && before != 1 ? 2u : 0u)
                | (action == ServiceAction.Start && before != 4 || action == ServiceAction.Restart && (i == 0 || before == 4) ? 4u : 0u);
            if (UInt(fixedRow, 0) != i || flags > 15 || before is not (1 or 4 or 7) || desired != (action == ServiceAction.Stop ? 1u : 4u)
                || flags != expectedFlags || action != ServiceAction.Stop && before == 7 || dependencies > 64 || UInt(fixedRow, 52) != 0
                || ((flags & 2) == 0 ? stopOrder != uint.MaxValue : stopOrder >= rows.Length || !stops.Add(stopOrder))
                || ((flags & 4) == 0 ? startOrder != uint.MaxValue : startOrder >= rows.Length || !starts.Add(startOrder))) throw Invalid("Neplatná položka plánu služeb.");
            string name = reader.Text(UInt(fixedRow, 24)), title = reader.Text(UInt(fixedRow, 28));
            string command = reader.Text(UInt(fixedRow, 32)), account = reader.Text(UInt(fixedRow, 36));
            if (name.Length is < 1 or > 256 || !names.Add(name)) throw Invalid("Neplatný nebo duplicitní název služby.");
            var required = new string[dependencies];
            for (int d = 0; d < required.Length; ++d) { required[d] = reader.Text(reader.UInt()); if (required[d].Length == 0) throw Invalid("Prázdná závislost služby."); }
            rows[i] = new(i, (ServicePlanFlags)flags, before, desired, stopOrder, startOrder, name, title, command, account, UInt(fixedRow, 44), UInt(fixedRow, 48), required);
        }
        reader.Finished();
        if (!string.Equals(rows[0].Name, expectedName, StringComparison.OrdinalIgnoreCase)) throw Invalid("Plán patří jiné službě.");
        return new(action, ULong(request, 16), header.NonceLow, header.NonceHigh, rows);
    }
    internal static ServiceActionItem[] DecodeItems(ServiceActionHeader header, ReadOnlySpan<byte> payload, ServiceActionPlan plan, bool accepted)
    {
        if (header.Phase is not (ServiceActionPhase.Progress or ServiceActionPhase.Result) || payload.Length != header.Payload) throw Invalid("Neúplný výsledek služeb.");
        var rows = new ServiceActionItem[header.Count];
        for (int i = 0; i < rows.Length; ++i) {
            var row = payload.Slice(i * 56, 56); uint outcome = UInt(row, 4), state = UInt(row, 8), flags = UInt(row, 16);
            long observed = BinaryPrimitives.ReadInt64LittleEndian(row[48..]);
            uint expectedSteps = (plan.Nodes[i].Flags.HasFlag(ServicePlanFlags.StopRequired) ? 2u : 0u)
                | (plan.Nodes[i].Flags.HasFlag(ServicePlanFlags.StartRequired) ? 8u : 0u);
            if (UInt(row, 0) != i || outcome > 7 || flags > 127 || UInt(row, 44) != 0 || observed < 0
                || ((flags & 32) != 0 ? state is < 1 or > 7 : state != 0 || UInt(row, 28) != 0 || UInt(row, 32) != 0 || UInt(row, 36) != 0 || UInt(row, 40) != 0)
                || !accepted && (flags & 79) != 0 || (flags & 5) != 0 && (flags & 64) == 0
                || !plan.Nodes[i].Flags.HasFlag(ServicePlanFlags.StopRequired) && (flags & 3) != 0
                || !plan.Nodes[i].Flags.HasFlag(ServicePlanFlags.StartRequired) && (flags & 12) != 0
                || outcome == 2 && (flags & expectedSteps) != expectedSteps || outcome == 1 && expectedSteps != 0
                || header.Status == ServiceActionStatus.Ok && header.Phase == ServiceActionPhase.Result
                    && (outcome is not (1 or 2) || state != plan.Nodes[i].DesiredState || (flags & 48) != 48)) throw Invalid("Neplatný výsledek služby.");
            rows[i] = new((uint)i, (ServiceActionOutcome)outcome, state, UInt(row, 12), (ServiceResultFlags)flags,
                UInt(row, 20), UInt(row, 24), UInt(row, 28), UInt(row, 32), UInt(row, 36), UInt(row, 40), observed);
        }
        return rows;
    }
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> data;
        private int at;
        internal Reader(ReadOnlySpan<byte> value) { data = value; at = 0; }
        internal ReadOnlySpan<byte> Take(int length) { if (length > data.Length - at) throw Invalid("Neúplný rámec služeb."); var value = data.Slice(at, length); at += length; return value; }
        internal uint UInt() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        internal string Text(uint chars) {
            if (chars > 32767) throw Invalid("Text služby překročil limit.");
            try { var value = Utf16.GetString(Take(checked((int)chars * 2))); return value.Contains('\0') ? throw Invalid("Nulový znak v textu služby.") : value; }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Neplatný Unicode text služby.", error); }
        }
        internal void Finished() { if (at != data.Length) throw Invalid("Data za koncem rámce služeb."); }
    }
    private static uint UInt(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    private static ulong ULong(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);
    private static void Write(Span<byte> bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[at..], value);
    private static void Write(Span<byte> bytes, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes[at..], value);
    private static InvalidDataException Invalid(string message) => new(message);
}
