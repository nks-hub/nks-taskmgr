using System.Buffers.Binary;
using System.Text;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal enum ProcessDetailsCommand : ushort { Threads = 1, Modules = 2, Signature = 3, Memory = 4, Handles = 5, HandleInfo = 6, Owners = 7 }
internal enum ProcessDetailsState : uint { Ok, AccessDenied, Unsupported, Unavailable, TargetChanged, InvalidRequest, LimitExceeded, Error }
internal enum ProcessArchitecture : uint { Unknown, X86, X64, Arm64 }
internal enum ModuleSignatureState : uint { Valid, Unsigned, Untrusted, Unavailable }
internal enum ModuleSignatureSource : uint { None, Embedded, Catalog }

internal sealed record ProcessThreadInfo(uint Id, int BasePriority, int RelativePriority, ulong Created100ns,
    ulong Kernel100ns, ulong User100ns, SampleState TimesState, uint TimesError, SampleState PriorityState, uint PriorityError)
{
    public override string ToString() => "TID " + Id + " · základní priorita " + BasePriority;
}
internal sealed record ProcessModuleInfo(ulong BaseAddress, ulong ImageSize, string Name, string Path)
{
    public override string ToString()
    {
        string name = Name.Length == 0 ? "(název nedostupný)" : Name.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        int length = Math.Min(140, name.Length);
        if (length < name.Length && char.IsHighSurrogate(name[length - 1])) --length;
        return "0x" + BaseAddress.ToString("X16") + " · " + (length == name.Length ? name : name[..length] + "…");
    }
}
internal sealed record ModuleSignatureInfo(ModuleSignatureState State, ModuleSignatureSource Source, int TrustCode, string Path, string Publisher);
internal sealed record ProcessDetailsSnapshot(ProcessDetailsCommand Command, ProcessDetailsState State, uint Error, int NtStatus,
    ProcessArchitecture Architecture, bool PartialMetrics, ProcessThreadInfo[] Threads, ProcessModuleInfo[] Modules,
    ModuleSignatureInfo? Signature, DateTimeOffset ReceivedAt)
{
    internal ProcessMemoryRegion[] Memory { get; init; } = [];
    internal ProcessHandleInfo[] Handles { get; init; } = [];
    internal ProcessHandleObservation? HandleInfo { get; init; }
    internal ProcessOwnerInfo[] Owners { get; init; } = [];
}

internal static partial class ProcessDetailsProtocol
{
    private const uint Magic = 0x31444b4e;
    internal const int ResponseHeaderBytes = 80;
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);

    internal static byte[] Request(ProcessDetailsCommand command, ProcessKey key, ulong requestId, ProcessModuleInfo? module, ulong? handle = null)
    {
        bool owners = command == ProcessDetailsCommand.Owners;
        if (key.Boot.IsEmpty || key.Kind is not (GenerationKind.CreationTime or GenerationKind.SequenceNumber)
            || (owners ? key.Pid != 0 || key.Generation != 0 : key.Generation == 0))
            throw new ArgumentException("Proces nemá ověřitelnou identitu.", nameof(key));
        int pathChars = module?.Path.Length ?? 0;
        if (command == ProcessDetailsCommand.Signature && (module is null || pathChars is 0 or > 32767 || module.Path.Contains('\0')))
            throw new ArgumentException("Podpis vyžaduje cestu vybraného modulu.", nameof(module));
        if (command == ProcessDetailsCommand.HandleInfo && handle is null) throw new ArgumentException("Chybí hodnota handle.", nameof(handle));
        int payload = command == ProcessDetailsCommand.Signature ? checked(12 + pathChars * 2) : command == ProcessDetailsCommand.HandleInfo ? 8 : 0;
        var bytes = new byte[56 + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)command);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)payload);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), requestId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), key.Boot.Low);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), key.Boot.High);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), key.Pid);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), (uint)key.Kind);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), key.Generation);
        if (command == ProcessDetailsCommand.Signature) {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56), module!.BaseAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), (uint)pathChars);
            try { Utf16.GetBytes(module.Path, bytes.AsSpan(68)); }
            catch (EncoderFallbackException error) { throw new ArgumentException("Cesta modulu obsahuje neplatný Unicode text.", nameof(module), error); }
        }
        if (command == ProcessDetailsCommand.HandleInfo) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56), handle!.Value);
        return bytes;
    }

    internal static int PayloadLength(ReadOnlySpan<byte> header, ReadOnlySpan<byte> request)
    {
        if (header.Length != ResponseHeaderBytes || request.Length < 56
            || BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
            || !header[..8].SequenceEqual(request[..8]) || !header.Slice(16, 40).SequenceEqual(request.Slice(16, 40))
            || UInt(header, 76) != 0) throw Invalid("Pracovník vrátil neplatnou hlavičku nebo jiný proces.");
        uint length = UInt(header, 8), state = UInt(header, 12), architecture = UInt(header, 64), rows = UInt(header, 68), flags = UInt(header, 72);
        var command = (ProcessDetailsCommand)BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        if (length > MaximumResponseBytes || state > (uint)ProcessDetailsState.Error || architecture > (uint)ProcessArchitecture.Arm64)
            throw Invalid("Pracovník překročil protokol nebo datový limit.");
        uint expectedFlags = command == ProcessDetailsCommand.Signature ? 2u : 0u;
        uint allowedFlags = command is ProcessDetailsCommand.Threads or ProcessDetailsCommand.HandleInfo or ProcessDetailsCommand.Owners ? 1u : expectedFlags;
        if ((flags & ~allowedFlags) != 0 || (command == ProcessDetailsCommand.Signature && flags != expectedFlags))
            throw Invalid("Pracovník vrátil neplatné příznaky.");
        if (state != 0 && (length != 0 || rows != 0)) throw Invalid("Chybový výsledek obsahuje neověřená data.");
        if (command == ProcessDetailsCommand.Threads && (rows > 16384 || state == 0 && length != rows * 56)
            || command == ProcessDetailsCommand.Modules && rows > 4096
            || command is ProcessDetailsCommand.Signature or ProcessDetailsCommand.HandleInfo && state == 0 && rows != 1
            || command == ProcessDetailsCommand.Memory && (rows > 65536 || state == 0 && length != rows * 40)
            || command == ProcessDetailsCommand.Handles && (rows > 65536 || state == 0 && length != rows * 24)
            || command == ProcessDetailsCommand.Owners && rows > 32768)
            throw Invalid("Pracovník vrátil neplatný počet záznamů.");
        return (int)length;
    }

    internal static ProcessDetailsSnapshot Response(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> request, ProcessModuleInfo? selected)
    {
        if (PayloadLength(header, request) != payload.Length) throw Invalid("Neúplný výsledek pracovníka.");
        var command = (ProcessDetailsCommand)BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        if (command is >= ProcessDetailsCommand.Memory and <= ProcessDetailsCommand.Owners) return ReadObjects(header, payload, request);
        var state = (ProcessDetailsState)UInt(header, 12);
        var architecture = (ProcessArchitecture)UInt(header, 64);
        uint rowCount = UInt(header, 68);
        var threads = state == ProcessDetailsState.Ok && command == ProcessDetailsCommand.Threads ? new ProcessThreadInfo[rowCount] : [];
        var modules = state == ProcessDetailsState.Ok && command == ProcessDetailsCommand.Modules ? new ProcessModuleInfo[rowCount] : [];
        ModuleSignatureInfo? signature = null;
        if (state == ProcessDetailsState.Ok) {
            int offset = 0;
            if (command == ProcessDetailsCommand.Threads) {
                var ids = new HashSet<uint>();
                for (int i = 0; i < threads.Length; ++i) {
                    var row = payload.Slice(offset, 56); offset += 56;
                    uint id = UInt(row, 0); var times = State(UInt(row, 40)); var priority = State(UInt(row, 48));
                    if (id == 0 || !ids.Add(id) || UInt(row, 4) != UInt(request, 40)
                        || times == SampleState.Valid && UInt(row, 44) != 0 || priority == SampleState.Valid && UInt(row, 52) != 0
                        || times != SampleState.Valid && (ULong(row, 16) != 0 || ULong(row, 24) != 0 || ULong(row, 32) != 0)
                        || priority != SampleState.Valid && Int(row, 12) != 0)
                        throw Invalid("Neplatný záznam vlákna.");
                    threads[i] = new(id, Int(row, 8), Int(row, 12), ULong(row, 16), ULong(row, 24), ULong(row, 32), times, UInt(row, 44), priority, UInt(row, 52));
                }
            } else if (command == ProcessDetailsCommand.Modules) {
                var bases = new HashSet<ulong>();
                for (int i = 0; i < modules.Length; ++i) {
                    var row = Take(payload, ref offset, 24);
                    ulong address = ULong(row, 0), size = ULong(row, 8);
                    uint nameChars = UInt(row, 16), pathChars = UInt(row, 20);
                    if (address == 0 || size == 0 || address > ulong.MaxValue - size || !bases.Add(address)) throw Invalid("Neplatná adresa nebo velikost modulu.");
                    modules[i] = new(address, size, Text(payload, ref offset, nameChars), Text(payload, ref offset, pathChars));
                }
            } else {
                var row = Take(payload, ref offset, 24);
                uint signatureState = UInt(row, 0), source = UInt(row, 4), pathChars = UInt(row, 16), publisherChars = UInt(row, 20);
                int trust = Int(row, 8);
                if (signatureState > 3 || source > 2 || UInt(row, 12) != 0 || signatureState == 0 && (source == 0 || trust != 0))
                    throw Invalid("Neplatný stav podpisu souboru.");
                string path = Text(payload, ref offset, pathChars), publisher = Text(payload, ref offset, publisherChars);
                if (selected is null || !string.Equals(selected.Path, path, StringComparison.OrdinalIgnoreCase)) throw Invalid("Podpis patří jinému souboru.");
                signature = new((ModuleSignatureState)signatureState, (ModuleSignatureSource)source, trust, path, publisher);
            }
            if (offset != payload.Length) throw Invalid("Výsledek obsahuje data mimo rámec.");
        }
        return new(command, state, UInt(header, 56), Int(header, 60), architecture, (UInt(header, 72) & 1) != 0, threads, modules, signature, DateTimeOffset.UtcNow);
    }

    private static SampleState State(uint value) => value <= (uint)SampleState.Gap ? (SampleState)value : throw Invalid("Neplatný stav měření.");
    private static uint UInt(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(value[offset..]);
    private static int Int(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadInt32LittleEndian(value[offset..]);
    private static ulong ULong(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(value[offset..]);
    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> value, ref int offset, int length)
    {
        if (length > value.Length - offset) throw Invalid("Neúplný datový rámec.");
        var part = value.Slice(offset, length); offset += length; return part;
    }
    private static string Text(ReadOnlySpan<byte> value, ref int offset, uint characters)
    {
        if (characters > 32767) throw Invalid("Text pracovníka překročil limit.");
        var bytes = Take(value, ref offset, checked((int)characters * 2));
        try {
            string text = Utf16.GetString(bytes);
            return text.Contains('\0') ? throw Invalid("Text pracovníka obsahuje nulový znak.") : text;
        } catch (DecoderFallbackException error) { throw new InvalidDataException("Pracovník vrátil neplatný Unicode text.", error); }
    }
    private static InvalidDataException Invalid(string message) => new(message);
}
