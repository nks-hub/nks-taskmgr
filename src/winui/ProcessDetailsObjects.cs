using System.Buffers.Binary;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed record ProcessMemoryRegion(ulong BaseAddress, ulong AllocationBase, ulong RegionSize, uint State, uint Protection, uint AllocationProtection, uint Type)
{
    internal static string StateText(uint state) => state switch { 0x1000 => Loc.T("Potvrzená"), 0x2000 => Loc.T("Rezervovaná"), 0x10000 => Loc.T("Volná"), _ => Loc.F("Stav 0x{0}", state.ToString("X")) };
    internal static string TypeText(uint type) => type switch { 0x20000 => Loc.T("Soukromá"), 0x40000 => Loc.T("Mapovaná"), 0x1000000 => Loc.T("Obraz souboru"), 0 => Loc.T("Neuveden"), _ => Loc.F("Typ 0x{0}", type.ToString("X")) };
    public override string ToString() => "0x" + BaseAddress.ToString("X16") + " · " + RegionSize.ToString("N0") + " B · " + StateText(State);
}
internal sealed record ProcessHandleInfo(ulong Value, uint GrantedAccess, uint TypeIndex, uint Attributes)
{
    public override string ToString() => "0x" + Value.ToString("X") + " · " + Loc.F("typ {0} · přístup 0x{1}", TypeIndex, GrantedAccess.ToString("X8"));
}
// Kind 1/2: Id is the PID/TID and Value0 its creation FILETIME. Kind 3: Value0 is the volume serial, Value1/2 the 128-bit file ID.
internal sealed record ProcessHandleFingerprint(uint Kind, uint Id, ulong Value0, ulong Value1, ulong Value2)
{
    internal static readonly ProcessHandleFingerprint None = new(0, 0, 0, 0, 0);
    internal bool Present => Kind != 0;
    internal string Text => Kind switch {
        1 => Loc.F("PID {0}, vznik 0x{1}", Id, Value0.ToString("X")), 2 => Loc.F("TID {0}, vznik 0x{1}", Id, Value0.ToString("X")),
        3 => Loc.F("svazek 0x{0}, ID souboru 0x{1}", Value0.ToString("X"), Value2.ToString("X16") + Value1.ToString("X16")), _ => Loc.T("nedostupný")
    };
}
internal sealed record ProcessHandleObservation(ulong Value, string Type, string Name, SampleState TypeState, SampleState NameState, int TypeNtStatus, int NameNtStatus,
    ProcessHandleFingerprint Fingerprint);
internal sealed record ProcessOwnerInfo(ProcessKey Key, string Name);

internal static partial class ProcessDetailsProtocol
{
    private static ProcessDetailsSnapshot ReadObjects(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> request)
    {
        var command = (ProcessDetailsCommand)BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        var state = (ProcessDetailsState)UInt(header, 12);
        var result = new ProcessDetailsSnapshot(command, state, UInt(header, 56), Int(header, 60), (ProcessArchitecture)UInt(header, 64),
            (UInt(header, 72) & 1) != 0, [], [], null, DateTimeOffset.UtcNow);
        if (state != ProcessDetailsState.Ok) return result;
        int count = checked((int)UInt(header, 68)), offset = 0;
        switch (command) {
            case ProcessDetailsCommand.Memory: {
                var regions = new ProcessMemoryRegion[count]; ulong previousEnd = 0;
                for (int i = 0; i < regions.Length; ++i) {
                    var row = Take(payload, ref offset, 40);
                    ulong address = ULong(row, 0), size = ULong(row, 16);
                    if (size == 0 || address > ulong.MaxValue - size || address < previousEnd
                        || result.Architecture == ProcessArchitecture.X86 && address + size > 0x1_0000_0000UL)
                        throw Invalid(Loc.T("Neplatná nebo překrývající se oblast paměti."));
                    regions[i] = new(address, ULong(row, 8), size, UInt(row, 24), UInt(row, 28), UInt(row, 32), UInt(row, 36));
                    previousEnd = address + size;
                }
                result = result with { Memory = regions }; break;
            }
            case ProcessDetailsCommand.Handles: {
                var handles = new ProcessHandleInfo[count]; var values = new HashSet<ulong>();
                for (int i = 0; i < handles.Length; ++i) {
                    var row = Take(payload, ref offset, 24); ulong value = ULong(row, 0);
                    if (UInt(row, 20) != 0 || !values.Add(value)) throw Invalid(Loc.T("Neplatný nebo duplicitní handle."));
                    handles[i] = new(value, UInt(row, 8), UInt(row, 12), UInt(row, 16));
                }
                result = result with { Handles = handles }; break;
            }
            case ProcessDetailsCommand.HandleInfo: {
                var row = Take(payload, ref offset, 64); ulong value = ULong(row, 0);
                if (request.Length != 64 || value != ULong(request, 56)) throw Invalid(Loc.T("Pozorování patří jinému handle."));
                var print = new ProcessHandleFingerprint(UInt(row, 32), UInt(row, 36), ULong(row, 40), ULong(row, 48), ULong(row, 56));
                var typeState = State(UInt(row, 16)); var nameState = State(UInt(row, 20));
                string type = Text(payload, ref offset, UInt(row, 8)), name = Text(payload, ref offset, UInt(row, 12));
                int typeStatus = Int(row, 24), nameStatus = Int(row, 28);
                if (typeState != SampleState.Valid && type.Length != 0 || nameState != SampleState.Valid && name.Length != 0
                    || typeState == SampleState.Valid && (type.Length == 0 || typeStatus != 0) || nameState == SampleState.Valid && nameStatus != 0
                    || !FingerprintValid(print, type))
                    throw Invalid(Loc.T("Neplatný stav názvu, typu nebo otisku objektu."));
                result = result with { HandleInfo = new(value, type, name, typeState, nameState, typeStatus, nameStatus, print) }; break;
            }
            case ProcessDetailsCommand.Owners: {
                var owners = new ProcessOwnerInfo[count]; var pids = new HashSet<uint>();
                var boot = new BootIdentifier(ULong(header, 24), ULong(header, 32));
                for (int i = 0; i < owners.Length; ++i) {
                    var row = Take(payload, ref offset, 24); uint pid = UInt(row, 0), kind = UInt(row, 4); ulong generation = ULong(row, 8);
                    if (kind is not (1 or 2) || generation == 0 || UInt(row, 20) != 0 || !pids.Add(pid)) throw Invalid(Loc.T("Neplatná nebo duplicitní identita vlastníka."));
                    owners[i] = new(new(boot, pid, (GenerationKind)kind, generation), Text(payload, ref offset, UInt(row, 16)));
                }
                result = result with { Owners = owners }; break;
            }
        }
        if (offset != payload.Length) throw Invalid(Loc.T("Výsledek obsahuje data mimo rámec."));
        return result;
    }

    internal static bool FingerprintValid(ProcessHandleFingerprint print, string type) => print.Kind switch {
        0 => print == ProcessHandleFingerprint.None,
        1 or 2 => type == (print.Kind == 1 ? "Process" : "Thread") && print.Id != 0 && print.Value0 != 0 && print.Value1 == 0 && print.Value2 == 0,
        3 => type == "File" && print.Id == 0 && (print.Value0 | print.Value1 | print.Value2) != 0,
        _ => false
    };
}
