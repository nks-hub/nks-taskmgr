using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal enum ProcessDumpMode : ushort { Mini, Full }
internal enum ProcessDumpState : uint { Captured, Denied, Unsupported, TargetChanged, Unavailable, LimitExceeded, Error, Canceled, InvalidRequest }
internal readonly record struct DumpFileIdentity(ulong Volume, ulong Low, ulong High);
internal sealed record ProcessDumpTarget(ProcessKey Key, ulong Created100ns);
internal sealed record ProcessDumpResult(ProcessDumpState State, uint Error, uint CaptureFlags, ProcessArchitecture Architecture,
    ulong FileBytes, ulong ObservedCreated100ns, string Sha256, DumpFileIdentity FileId)
{
    internal string? SavedPath { get; init; }
    internal string? RecoveryWarning { get; init; }
}

internal static class ProcessDumpProtocol
{
    internal const ulong MaximumFileBytes = 32ul * 1024 * 1024 * 1024;
    internal const int ResponseBytes = 168;
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);
    internal static byte[] Request(ProcessDumpTarget target, ProcessDumpMode mode, ProcessDumpFile file)
    {
        var key = target.Key;
        if (key.Boot.IsEmpty || key.Pid == 0 || key.Kind is not (GenerationKind.CreationTime or GenerationKind.SequenceNumber)
            || key.Generation == 0 || target.Created100ns == 0 || mode is not (ProcessDumpMode.Mini or ProcessDumpMode.Full))
            throw new ArgumentException(Loc.T("Proces nemá ověřitelnou identitu."));
        string destination = file.Destination, temporary = file.Temporary;
        if (destination.Length is 0 or > 32760 || temporary.Length is 0 or > 32760 || destination.Contains('\0') || temporary.Contains('\0'))
            throw new ArgumentException(Loc.T("Cesta dumpu je příliš dlouhá nebo neplatná."));
        var bytes = new byte[112 + (destination.Length + temporary.Length) * 2];
        Write(bytes, 0, 0x31444d4eu); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)mode);
        Write(bytes, 8, (uint)(bytes.Length - 112)); Write(bytes, 12, (uint)destination.Length);
        RandomNumberGenerator.Fill(bytes.AsSpan(16, 24));
        if (ULong(bytes, 16) == 0) Write(bytes, 16, 1ul);
        if (ULong(bytes, 24) == 0 && ULong(bytes, 32) == 0) Write(bytes, 24, 1ul);
        Write(bytes, 40, key.Boot.Low); Write(bytes, 48, key.Boot.High); Write(bytes, 56, key.Pid); Write(bytes, 60, (uint)key.Kind);
        Write(bytes, 64, key.Generation); Write(bytes, 72, target.Created100ns); Write(bytes, 80, unchecked((ulong)file.Handle.DangerousGetHandle()));
        Write(bytes, 88, MaximumFileBytes); Write(bytes, 96, (uint)temporary.Length); Write(bytes, 100, (uint)Environment.ProcessId);
        Utf16.GetBytes(destination, bytes.AsSpan(112)); Utf16.GetBytes(temporary, bytes.AsSpan(112 + destination.Length * 2));
        return bytes;
    }

    internal static ProcessDumpResult Response(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> request, ProcessArchitecture architecture, DumpFileIdentity expectedFile)
    {
        if (bytes.Length != ResponseBytes || request.Length < 112 || !bytes[..8].SequenceEqual(request[..8]) || UInt(bytes, 8) != 0
            || !bytes.Slice(16, 64).SequenceEqual(request.Slice(16, 64)) || UInt(bytes, 92) != 0)
            throw new InvalidDataException(Loc.T("Odpověď dumpu nepatří požadavku nebo vybranému procesu."));
        var state = (ProcessDumpState)UInt(bytes, 12); var actualArchitecture = (ProcessArchitecture)UInt(bytes, 88);
        uint flags = UInt(bytes, 84), expectedFlags = BinaryPrimitives.ReadUInt16LittleEndian(request[6..]) == 1 ? 0x1802u : 0;
        ulong size = ULong(bytes, 96), created = ULong(bytes, 104);
        var file = new DumpFileIdentity(ULong(bytes, 144), ULong(bytes, 152), ULong(bytes, 160));
        if ((uint)state > (uint)ProcessDumpState.InvalidRequest || (uint)actualArchitecture > (uint)ProcessArchitecture.X64
            || flags != 0 && flags != expectedFlags || size > MaximumFileBytes
            || state == ProcessDumpState.Captured && (UInt(bytes, 80) != 0 || flags != expectedFlags || actualArchitecture != architecture
                || size == 0 || created != ULong(request, 72) || file != expectedFile || bytes.Slice(112, 32).IndexOfAnyExcept((byte)0) < 0))
            throw new InvalidDataException(Loc.T("Pracovník vrátil neplatný výsledek dumpu."));
        return new(state, UInt(bytes, 80), flags, actualArchitecture, size, created, Convert.ToHexString(bytes.Slice(112, 32)), file);
    }
    private static uint UInt(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    private static ulong ULong(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);
    private static void Write(Span<byte> bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[at..], value);
    private static void Write(Span<byte> bytes, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes[at..], value);
}
