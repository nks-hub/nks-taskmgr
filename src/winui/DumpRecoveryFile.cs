using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace NksTaskMgr.WinUI;

internal sealed partial class ProcessDumpFile
{
    internal string ParentPath => Destination[..(Destination.LastIndexOf('\\') + 1)];
    internal SafeFileHandle Parent => directories[^1];
    internal static ProcessDumpFile HoldDirectory(string path) => new(Path.Combine(path, ".nks-directory-lease"));
    private ProcessDumpFile(string directoryAnchor)
    {
        fileName = ""; preserve = true;
        try { fileName = PrepareDirectories(directoryAnchor); }
        catch { Dispose(); throw; }
    }
    internal static ProcessDumpFile Reopen(DumpRecoveryRecord record, string proof, bool destination, bool writable) => new(record, proof, destination, writable);
    internal static ProcessDumpFile HoldRecoveryParent(DumpRecoveryRecord record)
    {
        var held = HoldDirectory(Path.GetDirectoryName(record.Destination[4..])!);
        if (ReadIdentity(held.Parent) == record.ParentId) return held;
        held.Dispose(); throw new IOException("Rodičovský adresář nelze ověřit. Soubor i záznam zůstanou zachované.");
    }
    private ProcessDumpFile(DumpRecoveryRecord record, string proof, bool destination, bool writable)
    {
        fileName = ""; preserve = true;
        try {
            fileName = PrepareDirectories(record.Destination[4..]);
            if (ReadIdentity(Parent) != record.ParentId) throw new IOException("Rodičovský adresář má jinou identitu. Soubor zůstane zachovaný.");
            Temporary = record.Temporary; Identity = record.FileId; Journal = DumpRecoveryJournal.Attach(record, proof);
            Handle = OpenRelative(Parent, Path.GetFileName(destination ? record.Destination : record.Temporary), writable ? 0xc0110000u : 0x80100000u, 0, 1, 0x00200060, 0);
            VerifyOwned(record);
            if (!StringComparer.OrdinalIgnoreCase.Equals(FinalName(Handle), destination ? record.Destination : record.Temporary))
                throw new IOException("Cesta souboru neodpovídá záznamu. Soubor zůstane zachovaný.");
        } catch { Dispose(); throw; }
    }
    internal void VerifyOwned(DumpRecoveryRecord record)
    {
        if (ReadIdentity(Parent) != record.ParentId || ReadIdentity(Handle) != record.FileId || Owner(Handle) != record.FileOwnerSid)
            throw new IOException("Identita nebo vlastník souboru nesouhlasí. Soubor zůstane zachovaný.");
        CheckSingleFile(Handle);
    }
    internal string Hash(CancellationToken cancellation)
    {
        long length = RandomAccess.GetLength(Handle);
        if (length < 0 || (ulong)length > ProcessDumpProtocol.MaximumFileBytes) throw new IOException("Velikost souboru překračuje limit obnovy.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024]; long offset = 0;
        while (offset < length) {
            cancellation.ThrowIfCancellationRequested();
            int count = RandomAccess.Read(Handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
            if (count == 0) throw new IOException("Soubor se během ověřování změnil.");
            hash.AppendData(buffer.AsSpan(0, count)); offset += count;
        }
        if (RandomAccess.GetLength(Handle) != length) throw new IOException("Velikost souboru se během ověřování změnila.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal void DeleteRecovered(DumpRecoveryRecord record)
    {
        DumpRecoveryProcesses.EnsureStopped(record); VerifyOwned(record);
        DeleteEntry(Handle); Handle.Dispose(); Journal!.Remove();
    }
    internal static SafeFileHandle OpenEntry(SafeFileHandle parent, string name, uint access = 0x80100000u, uint share = 1, uint disposition = 1)
        => OpenRelative(parent, name, access, share, disposition, 0x00200060, 0x2000);
    internal static DumpFileIdentity FileIdentity(SafeFileHandle file) => ReadIdentity(file);
    internal static unsafe void RenameEntry(SafeFileHandle file, SafeFileHandle parent, string name, bool replace)
    {
        var data = new byte[20 + name.Length * 2]; data[0] = replace ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), parent.DangerousGetHandle());
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)name.Length * 2); System.Text.Encoding.Unicode.GetBytes(name, data.AsSpan(20));
        fixed (byte* bytes = data) if (!SetFileInformationByHandle(file, 3, bytes, (uint)data.Length)) throw LastError();
    }
    internal static unsafe void DeleteEntry(SafeFileHandle file) { byte delete = 1; if (!SetFileInformationByHandle(file, 4, &delete, 1)) throw LastError(); }
    internal static unsafe void CheckSingleFile(SafeFileHandle file)
    {
        var bytes = stackalloc byte[52];
        if (!GetFileInformationByHandle(file, bytes)) throw LastError();
        var span = new ReadOnlySpan<byte>(bytes, 52);
        if ((BinaryPrimitives.ReadUInt32LittleEndian(span) & 0x410) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(span[40..]) != 1)
            throw new IOException("Soubor má nepovolený typ nebo více odkazů. Zůstane zachovaný.");
    }
    internal static string Owner(SafeFileHandle file)
    {
        uint error = GetSecurityInfo(file, 1, 1, out nint owner, out _, out _, out _, out nint descriptor);
        if (error != 0) throw new Win32Exception((int)error);
        try { return new SecurityIdentifier(owner).Value; }
        finally { if (descriptor != 0) LocalFree(descriptor); }
    }
    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(SafeFileHandle file, uint type, uint information, out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint allocation);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandle(SafeFileHandle file, void* information);
}
