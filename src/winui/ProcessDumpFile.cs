using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NksTaskMgr.WinUI;

internal sealed partial class ProcessDumpFile : IDisposable
{
    private readonly List<SafeFileHandle> directories = [];
    private readonly string fileName;
    private bool published, disposed, preserve;
    internal DumpRecoveryJournal? Journal { get; private set; }
    internal bool ReadyPreserved => preserve && !published && Journal?.Record.Phase == DumpRecoveryPhase.Ready;
    internal SafeFileHandle Handle { get; private set; } = new(0, true);
    internal string Destination { get; private set; } = "";
    internal string Temporary { get; private set; } = "";
    internal DumpFileIdentity Identity { get; private set; }

    internal ProcessDumpFile(string path, ProcessDumpTarget target, ProcessDumpMode mode)
    {
        fileName = "";
        try {
            fileName = PrepareDirectories(path);
            try {
                using var existing = OpenRelative(directories[^1], fileName, 0x100080, 7, 1, 0x00200060, 0);
                throw new IOException("Cílový soubor už existuje. Vyberte nový název; dump se nikdy nepřepisuje.");
            } catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { }
            string id = Guid.NewGuid().ToString("N"), temporaryName = ".nks-dump-" + id + ".tmp";
            Temporary = ParentPath + temporaryName;
            Journal = DumpRecoveryJournal.Create(id, target, mode, Destination, Temporary, ReadIdentity(directories[^1]));
            Handle = OpenRelative(directories[^1], temporaryName, 0xc0110000u, 0, 2, 0x00200060, 0x2100);
            Temporary = FinalName(Handle); Identity = ReadIdentity(Handle);
            Journal.TempOpened(Identity, Owner(Handle));
        } catch { Dispose(); throw; }
    }

    private string PrepareDirectories(string path)
    {
        path = Path.GetFullPath(path);
        if (path.Length is < 4 or > 32720 || path[1] != ':' || path[2] != '\\' || !char.IsAsciiLetter(path[0]))
            throw new ArgumentException("Dump ukládejte na místní disk s písmenem jednotky.");
        var parts = path[3..].Split('\\');
        if (parts.Any(part => part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("Cesta dumpu obsahuje nepovolený název.");
        uint directoryAccess = 0x1000a0u;
        var root = CreateFile("\\\\?\\" + path[..3], directoryAccess, 1, 0, 3, 0x02200000, 0);
        if (root.IsInvalid) { root.Dispose(); throw LastError(); }
        directories.Add(root); CheckDirectory(root);
        for (int i = 0; i < parts.Length - 1; ++i) {
            var next = OpenRelative(directories[^1], parts[i], 0x1000a0u, 1, 1, 0x00200021, 0);
            directories.Add(next); CheckDirectory(next);
        }
        string parent = FinalName(directories[^1]).TrimEnd('\\') + "\\";
        Destination = parent + parts[^1];
        return parts[^1];
    }

    internal ProcessDumpResult Publish(ProcessDumpResult result, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (result.State != ProcessDumpState.Captured || result.FileId != Identity || ReadIdentity(Handle) != Identity)
            throw new InvalidDataException("Dump neodpovídá vlastnímu dočasnému souboru.");
        if (!GetFileSizeEx(Handle, out long size)) throw LastError();
        if (size <= 0 || (ulong)size != result.FileBytes || (ulong)size > ProcessDumpProtocol.MaximumFileBytes)
            throw new InvalidDataException("Velikost dumpu neodpovídá výsledku pracovníka.");
        Journal?.Ready(result);
        var basic = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(basic.AsSpan(32), 0x2000);
        unsafe { fixed (byte* bytes = basic) if (!SetFileInformationByHandle(Handle, 0, bytes, (uint)basic.Length)) throw LastError(); }
        if (!FlushFileBuffers(Handle)) throw LastError();
        var rename = new byte[20 + fileName.Length * 2];
        BinaryPrimitives.WriteInt64LittleEndian(rename.AsSpan(8), directories[^1].DangerousGetHandle());
        BinaryPrimitives.WriteUInt32LittleEndian(rename.AsSpan(16), (uint)fileName.Length * 2);
        System.Text.Encoding.Unicode.GetBytes(fileName, rename.AsSpan(20));
        cancellation.ThrowIfCancellationRequested();
        unsafe { fixed (byte* bytes = rename) if (!SetFileInformationByHandle(Handle, 3, bytes, (uint)rename.Length)) throw LastError(); }
        published = true;
        string? warning = null;
        try { Journal?.Remove(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { warning = "Dump je uložený, ale záznam obnovy zůstal zachovaný: " + error.Message; }
        return result with { SavedPath = Destination.StartsWith("\\\\?\\", StringComparison.Ordinal) ? Destination[4..] : Destination, RecoveryWarning = warning };
    }

    private static unsafe SafeFileHandle OpenRelative(SafeFileHandle parent, string name, uint access, uint share, uint disposition, uint options, uint attributes)
    {
        fixed (char* text = name) {
            var unicode = new UnicodeName { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = text };
            var objects = new ObjectAttributes { Length = (uint)Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(),
                ObjectName = (nint)(&unicode), Attributes = 0x1040 };
            int status = NtCreateFile(out var file, access, in objects, out _, 0, attributes, share, disposition, options, 0, 0);
            if (status < 0) { file.Dispose(); throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))); }
            return file;
        }
    }

    private static unsafe void CheckDirectory(SafeFileHandle directory)
    {
        var bytes = stackalloc byte[8];
        if (!GetFileInformationByHandleEx(directory, 9, bytes, 8)) throw LastError();
        uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(bytes, 4));
        if ((attributes & 0x410) != 0x10) throw new IOException("Dump nelze uložit přes odkaz nebo přesměrovaný adresář.");
    }
    private static unsafe DumpFileIdentity ReadIdentity(SafeFileHandle file)
    {
        var bytes = stackalloc byte[24];
        if (!GetFileInformationByHandleEx(file, 18, bytes, 24)) throw LastError();
        var span = new ReadOnlySpan<byte>(bytes, 24);
        return new(BinaryPrimitives.ReadUInt64LittleEndian(span), BinaryPrimitives.ReadUInt64LittleEndian(span[8..]), BinaryPrimitives.ReadUInt64LittleEndian(span[16..]));
    }
    private static unsafe string FinalName(SafeFileHandle file)
    {
        uint length = GetFinalPathNameByHandle(file, null, 0, 0);
        if (length == 0) throw LastError();
        if (length > 32760) throw new IOException("Cesta dumpu je příliš dlouhá.");
        var chars = new char[length];
        fixed (char* text = chars) {
            uint written = GetFinalPathNameByHandle(file, text, length, 0);
            if (written == 0 || written >= length) throw LastError();
            return new string(text, 0, (int)written);
        }
    }
    private static Win32Exception LastError() => new(Marshal.GetLastPInvokeError());

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; int cleanupError = 0;
        if (!published && Journal?.Record.Phase == DumpRecoveryPhase.Ready) preserve = true;
        if (!Handle.IsInvalid && !Handle.IsClosed && !published && !preserve) {
            unsafe { byte delete = 1; if (!SetFileInformationByHandle(Handle, 4, &delete, 1)) cleanupError = Marshal.GetLastPInvokeError(); }
        }
        Handle.Dispose();
        try { if (cleanupError == 0 && !published && !preserve) Journal?.Remove(); }
        finally { for (int i = directories.Count - 1; i >= 0; --i) directories[i].Dispose(); }
        if (cleanupError != 0) throw new IOException("Vlastní dočasný soubor se nepodařilo odstranit: " + Temporary + " (Windows " + cleanupError + ").");
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeName { public ushort Length, MaximumLength; public char* Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public uint Length; public nint RootDirectory, ObjectName; public uint Attributes; public nint SecurityDescriptor, SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { public nint Status; public nuint Information; }
    [LibraryImport("ntdll.dll")]
    private static partial int NtCreateFile(out SafeFileHandle file, uint access, in ObjectAttributes objects, out IoStatus status, nint allocation,
        uint attributes, uint share, uint disposition, uint options, nint extendedAttributes, uint extendedLength);
    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, void* information, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* name, uint length, uint flags);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, void* information, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileSizeEx(SafeFileHandle file, out long size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle file);
}
