using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed record ProcessDumpQaInfo(int Threads, int Modules, int MemoryRanges, bool ProcessIdPresent,
    string Sha256, ulong Bytes, string Streams);

internal static partial class ProcessDumpQaReader
{
    private readonly record struct Location(uint Size, uint Rva);
    private readonly record struct MemoryRange(ulong Address, ulong Size);
    private const ulong MaximumQaFile = 512ul * 1024 * 1024;
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);

    internal static ProcessDumpQaInfo Inspect(string path, ProcessDumpTarget target, ProcessDumpMode mode,
        ProcessArchitecture architecture, ProcessDumpResult result, CancellationToken cancellation)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ProcessDumpFile.CheckSingleFile(file);
        ulong length = checked((ulong)RandomAccess.GetLength(file));
        Check(length is >= 32 and <= MaximumQaFile && length == result.FileBytes, "Unexpected QA dump size.");
        Check(Identity(file) == result.FileId, "Independent saved dump file identity differs from its result.");
        string hash = Hash(file, length, cancellation);
        Check(hash == result.Sha256, "Independent saved dump SHA256 differs from its result.");
        byte[] header = Read(file, length, 0, 32);
        Check(U32(header, 0) == 0x504d444d && (U32(header, 4) & 0xffff) == 0xa793, "Not a supported MDMP header.");
        uint count = U32(header, 8);
        Check(count is > 0 and <= 128, "Unexpected minidump directory count.");
        uint flags = mode == ProcessDumpMode.Full ? 0x1802u : 0;
        Check(U64(header, 24) == flags && result.CaptureFlags == flags, "Dump flags differ from the selected capture mode.");
        byte[] directory = Read(file, length, U32(header, 12), checked((int)count * 12));
        var streams = new Dictionary<uint, Location>();
        for (int i = 0; i < count; ++i) {
            uint type = U32(directory, i * 12);
            var location = new Location(U32(directory, i * 12 + 4), U32(directory, i * 12 + 8));
            Bounds(length, location.Rva, location.Size);
            if (type != 0) Check(streams.TryAdd(type, location), "Duplicate nonempty minidump stream type.");
        }
        Location Required(uint type, uint minimum) {
            Check(streams.TryGetValue(type, out var location) && location.Size >= minimum, "Required minidump stream is missing or truncated: " + type);
            return location;
        }
        var system = Required(7, 56);
        byte[] systemInfo = Read(file, length, system.Rva, 56);
        ushort expected = architecture == ProcessArchitecture.X86 ? (ushort)0 : (ushort)9;
        Check(BinaryPrimitives.ReadUInt16LittleEndian(systemInfo) == expected && result.Architecture == architecture,
            "SystemInfo or result architecture differs from the independently inspected child.");

        var memory = ReadMemory(file, length, streams, mode, cancellation);
        var threads = Required(3, 4);
        byte[] threadHeader = Read(file, length, threads.Rva, 4);
        uint threadCount = U32(threadHeader, 0);
        Check(threadCount is > 0 and <= 4096 && 4ul + threadCount * 48ul <= threads.Size, "Invalid ThreadList size.");
        byte[] threadRows = Read(file, length, (ulong)threads.Rva + 4, checked((int)threadCount * 48));
        int contexts = 0, stacks = 0;
        for (int i = 0; i < threadCount; ++i) {
            cancellation.ThrowIfCancellationRequested();
            int at = i * 48;
            Check(U32(threadRows, at) != 0, "Dump contains an invalid thread identifier.");
            ulong stackAddress = U64(threadRows, at + 24);
            uint stackSize = U32(threadRows, at + 32), stackRva = U32(threadRows, at + 36);
            if (stackSize != 0) { Bounds(length, stackRva, stackSize); ++stacks; }
            else if (stackAddress != 0 && memory.Any(range => stackAddress >= range.Address && stackAddress - range.Address < range.Size)) ++stacks;
            uint contextSize = U32(threadRows, at + 40), contextRva = U32(threadRows, at + 44);
            if (contextSize == 0) continue;
            Bounds(length, contextRva, contextSize);
            int needed = architecture == ProcessArchitecture.X86 ? 200 : 256;
            Check(contextSize >= needed, "Thread context is too short for its architecture.");
            byte[] context = Read(file, length, contextRva, needed);
            uint contextFlags = U32(context, architecture == ProcessArchitecture.X86 ? 0 : 48);
            uint architectureFlag = architecture == ProcessArchitecture.X86 ? 0x10000u : 0x100000u;
            Check((contextFlags & architectureFlag) != 0, "Thread context uses the wrong architecture.");
            ulong instruction = architecture == ProcessArchitecture.X86 ? U32(context, 184) : U64(context, 248);
            ulong stack = architecture == ProcessArchitecture.X86 ? U32(context, 196) : U64(context, 152);
            Check(instruction != 0 && stack != 0, "The gated child lacks an instruction pointer or stack pointer.");
            ++contexts;
        }
        Check(contexts > 0 && stacks > 0, "The gated child has no captured thread context or stack memory.");

        var modules = Required(4, 4);
        uint moduleCount = U32(Read(file, length, modules.Rva, 4), 0);
        Check(moduleCount is > 0 and <= 4096 && 4ul + moduleCount * 108ul <= modules.Size, "Invalid ModuleList size.");
        byte[] moduleRows = Read(file, length, (ulong)modules.Rva + 4, checked((int)moduleCount * 108));
        bool ownImage = false;
        for (int i = 0; i < moduleCount; ++i) {
            cancellation.ThrowIfCancellationRequested();
            int at = i * 108;
            Check(U64(moduleRows, at) != 0 && U32(moduleRows, at + 8) != 0, "Invalid module image range.");
            string name = String(file, length, U32(moduleRows, at + 20));
            ownImage |= string.Equals(Path.GetFileName(name), "cmd.exe", StringComparison.OrdinalIgnoreCase);
        }
        Check(ownImage, "The actual cmd fixture image is absent from ModuleList.");

        var misc = Required(15, 12);
        byte[] miscellaneous = Read(file, length, misc.Rva, 12);
        uint miscSize = U32(miscellaneous, 0), miscFlags = U32(miscellaneous, 4);
        Check(miscSize >= 12 && miscSize <= misc.Size, "Invalid MiscInfo size.");
        bool processIdPresent = (miscFlags & 1) != 0;
        Check(processIdPresent, "MiscInfo does not contain the required process identifier.");
        Check(U32(miscellaneous, 8) == target.Key.Pid, "Dump ProcessId differs from the held target.");
        if ((miscFlags & 2) != 0) {
            Check(miscSize >= 24 && misc.Size >= 24, "MiscInfo declares process times but does not contain them.");
            byte[] times = Read(file, length, misc.Rva, 24);
            // time_t stores whole UTC seconds; discard the FILETIME subsecond remainder.
            long createdSeconds = DateTimeOffset.FromFileTime(checked((long)target.Created100ns)).ToUnixTimeSeconds();
            Check(createdSeconds is >= 0 and <= uint.MaxValue && U32(times, 12) == (uint)createdSeconds,
                "Dump ProcessCreateTime differs from the held target's raw FILETIME at whole-second precision.");
        }
        if (mode == ProcessDumpMode.Full) {
            var information = Required(16, 16);
            byte[] info = Read(file, length, information.Rva, 16);
            Check(U32(info, 0) >= 16 && U32(info, 4) >= 48 && U64(info, 8) > 0
                && U64(info, 8) <= 1_000_000 && U32(info, 0) + U64(info, 8) * U32(info, 4) <= information.Size,
                "Invalid full dump memory-information list.");
            var threadInfo = Required(17, 12);
            byte[] detail = Read(file, length, threadInfo.Rva, 12);
            Check(U32(detail, 0) >= 12 && U32(detail, 4) >= 64 && U32(detail, 8) > 0
                && U32(detail, 8) <= 4096 && (ulong)U32(detail, 0) + (ulong)U32(detail, 4) * U32(detail, 8) <= threadInfo.Size,
                "Invalid full dump thread-information list.");
        }
        return new((int)threadCount, (int)moduleCount, memory.Count, processIdPresent, hash, length,
            string.Join(',', streams.Keys.Order()));
    }

    private static List<MemoryRange> ReadMemory(SafeFileHandle file, ulong length,
        IReadOnlyDictionary<uint, Location> streams, ProcessDumpMode mode, CancellationToken cancellation)
    {
        var result = new List<MemoryRange>();
        uint type = mode == ProcessDumpMode.Full ? 9u : 5u;
        Check(streams.TryGetValue(type, out var location), "Expected memory stream is absent.");
        if (mode == ProcessDumpMode.Full) {
            Check(location.Size >= 16, "Memory64List is truncated.");
            byte[] header = Read(file, length, location.Rva, 16);
            ulong count = U64(header, 0), data = U64(header, 8);
            Check(count is > 0 and <= 1_000_000 && 16 + count * 16 <= location.Size, "Invalid Memory64List count.");
            byte[] rows = Read(file, length, (ulong)location.Rva + 16, checked((int)count * 16));
            for (int i = 0; i < (int)count; ++i) {
                cancellation.ThrowIfCancellationRequested();
                ulong address = U64(rows, i * 16), size = U64(rows, i * 16 + 8);
                Check(size != 0 && address <= ulong.MaxValue - size, "Invalid Memory64List address range.");
                Bounds(length, data, size); data += size; result.Add(new(address, size));
            }
        } else {
            Check(location.Size >= 4, "MemoryList is truncated.");
            uint count = U32(Read(file, length, location.Rva, 4), 0);
            Check(count is > 0 and <= 1_000_000 && 4ul + count * 16ul <= location.Size, "Invalid MemoryList count.");
            byte[] rows = Read(file, length, (ulong)location.Rva + 4, checked((int)count * 16));
            for (int i = 0; i < count; ++i) {
                cancellation.ThrowIfCancellationRequested();
                ulong address = U64(rows, i * 16); uint size = U32(rows, i * 16 + 8), rva = U32(rows, i * 16 + 12);
                Check(size != 0 && address <= ulong.MaxValue - size, "Invalid MemoryList address range.");
                Bounds(length, rva, size); result.Add(new(address, size));
            }
        }
        return result;
    }

    private static string String(SafeFileHandle file, ulong length, uint rva)
    {
        uint bytes = U32(Read(file, length, rva, 4), 0);
        Check(bytes is > 0 and <= 65534 && (bytes & 1) == 0, "Invalid dump UTF16 name length.");
        return Utf16.GetString(Read(file, length, (ulong)rva + 4, (int)bytes));
    }
    private static string Hash(SafeFileHandle file, ulong length, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; ulong offset = 0;
        while (offset < length) {
            cancellation.ThrowIfCancellationRequested();
            int count = RandomAccess.Read(file, buffer.AsSpan(0, (int)Math.Min((ulong)buffer.Length, length - offset)), checked((long)offset));
            Check(count > 0, "Dump ended during independent hashing."); hash.AppendData(buffer.AsSpan(0, count)); offset += (uint)count;
        }
        Check((ulong)RandomAccess.GetLength(file) == length, "Dump size changed during inspection.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static byte[] Read(SafeFileHandle file, ulong length, ulong offset, int count)
    {
        Bounds(length, offset, checked((ulong)count));
        var bytes = new byte[count]; int read = 0;
        while (read < bytes.Length) {
            int current = RandomAccess.Read(file, bytes.AsSpan(read), checked((long)offset + read));
            Check(current > 0, "Dump stream ended before its declared size."); read += current;
        }
        return bytes;
    }
    private static unsafe DumpFileIdentity Identity(SafeFileHandle file)
    {
        byte* bytes = stackalloc byte[24];
        if (!GetFileInformationByHandleEx(file, 18, bytes, 24)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        var data = new ReadOnlySpan<byte>(bytes, 24);
        return new(U64(data, 0), U64(data, 8), U64(data, 16));
    }
    private static void Bounds(ulong length, ulong offset, ulong size) => Check(offset <= length && size <= length - offset, "Dump RVA exceeds the saved file.");
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, void* info, uint size);
}
