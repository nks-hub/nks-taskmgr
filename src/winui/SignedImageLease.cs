using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed unsafe partial class SignedImageLease : IDisposable
{
    private readonly List<SafeFileHandle> directories = [];
    private SafeFileHandle? image;
    private FileId id;
    internal FileId Identity => id;
    internal SafeFileHandle Handle => image ?? throw new ObjectDisposedException(nameof(SignedImageLease));
    internal string Path { get; private set; } = "";
    internal string ParentDirectoryPath => System.IO.Path.GetDirectoryName(Path) ?? throw new IOException("Chybí ověřený kořen souboru.");
    internal FileId ParentDirectoryId {
        get {
            if (directories.Count == 0 || !GetFileId(directories[^1], 18, out var value, (uint)sizeof(FileId)))
                throw new IOException("Identitu drženého kořenového adresáře nelze ověřit.");
            return value;
        }
    }
    internal ulong Length => checked((ulong)RandomAccess.GetLength(Handle));

    internal byte[] ReadAt(long offset, int count)
    {
        if (offset < 0 || count is < 0 or > 1024 * 1024 || (ulong)offset > Length || (ulong)count > Length - (ulong)offset)
            throw new InvalidDataException("Neplatný rozsah v podepsaném souboru.");
        var bytes = new byte[count]; int used = 0;
        while (used < count) {
            int read = RandomAccess.Read(Handle, bytes.AsSpan(used), offset + used);
            if (read == 0) throw new EndOfStreamException("Podepsaný soubor není úplný.");
            used += read;
        }
        return bytes;
    }
    internal byte[] Hash(ulong maximumBytes, CancellationToken cancellation)
    {
        ulong length = Length;
        if (length == 0 || length > maximumBytes) throw new InvalidDataException("Podepsaný soubor překračuje velikostní limit.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long offset = 0;
        while ((ulong)offset < length) {
            cancellation.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(Handle, buffer.AsSpan(0, (int)Math.Min((ulong)buffer.Length, length - (ulong)offset)), offset);
            if (read == 0) throw new EndOfStreamException("Podepsaný soubor není úplný.");
            hash.AppendData(buffer, 0, read); offset += read;
        }
        VerifyUnchanged(); return hash.GetHashAndReset();
    }
    internal void VerifyUnchanged()
    {
        if (!GetFileId(Handle, 18, out var current, (uint)sizeof(FileId)) || current != id)
            throw new IOException("Držený podepsaný soubor změnil identitu.");
    }

    internal static SignedImageLease Open(string path, byte[] pin)
    {
        if (pin.Length != 32) throw new ArgumentException("Neplatný připnutý klíč vydavatele.", nameof(pin));
        var lease = new SignedImageLease();
        try {
            lease.image = CreateFile(path, 0x80100020, 1, 0, 3, 0, 0);
            if (lease.image.IsInvalid || !GetFileId(lease.image, 18, out lease.id, (uint)sizeof(FileId))) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var buffer = new char[32768]; uint length;
            fixed (char* name = buffer) length = GetFinalPath(lease.image, name, (uint)buffer.Length, 1);
            if (length == 0 || length >= buffer.Length) throw new Win32Exception(Marshal.GetLastPInvokeError());
            lease.Path = new string(buffer, 0, checked((int)length));
            int rootEnd = lease.Path.IndexOf("}\\", StringComparison.Ordinal) + 1;
            if (!lease.Path.StartsWith("\\\\?\\Volume{", StringComparison.Ordinal) || rootEnd < 1 || GetDriveType(lease.Path[..(rootEnd + 1)]) != 3)
                throw new IOException("Podepsaný soubor musí být na ověřeném místním pevném svazku.");
            for (int slash = rootEnd; slash >= 0; slash = lease.Path.IndexOf('\\', slash + 1)) {
                string directory = lease.Path[..(slash == rootEnd ? slash + 1 : slash)];
                var handle = CreateFile(directory, 0xa0, 1, 0, 3, 0x02200000, 0);
                lease.directories.Add(handle);
                if (handle.IsInvalid || !GetAttributes(handle, 9, out var attributes, 8) || (attributes.Attributes & 0x410) != 0x10)
                    throw new IOException("Cestu podepsaného souboru nelze zamknout bez změnitelných odkazů.");
            }
            using var reopened = CreateFile(lease.Path, 0x80100020, 1, 0, 3, 0, 0);
            if (reopened.IsInvalid || !GetFileId(reopened, 18, out var actual, (uint)sizeof(FileId)) || actual != lease.id)
                throw new IOException("Podepsaný soubor se při ověření změnil.");
            lease.CheckSignature(pin); return lease;
        } catch { lease.Dispose(); throw; }
    }

    internal void VerifyProcess(Process process)
    {
        nint file = Handle.DangerousGetHandle();
        ProcessIdentityInfo key;
        try { key = NativeMonitor.QueryProcessIdentity(process.SafeHandle, GenerationKind.CreationTime); }
        catch (NativeMonitorException error) { throw new IOException("Identitu spuštěného podepsaného obrazu nelze ověřit: " + error.Message, error); }
        if (key.State != SampleState.Valid || key.Boot.IsEmpty || key.Generation == 0 || key.Pid != process.Id
            || NtQueryInformationProcess(process.SafeHandle, 44, ref file, (uint)sizeof(nint), 0) != 0
            || WaitForSingleObject(process.SafeHandle, 0) != 258
            || !GetFileId(Handle, 18, out var after, (uint)sizeof(FileId)) || after != id)
            throw new IOException("Spuštěný proces neodpovídá drženému podepsanému souboru. Požadavek nebyl odeslán.");
    }

    private void CheckSignature(byte[] pin)
    {
        nint name = Marshal.StringToCoTaskMemUni(Path);
        var file = new TrustFile { Size = (uint)sizeof(TrustFile), Path = name, Handle = image!.DangerousGetHandle() };
        var data = new TrustData { Size = (uint)sizeof(TrustData), Ui = 2, Revocation = 1, Choice = 1, File = (nint)(&file), Action = 1, Flags = 0x1080 };
        var policy = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try {
            if (WinVerifyTrust(0, ref policy, ref data) != 0) throw new IOException("Podpis souboru nebo řetězec důvěry není platný.");
            nint provider = WTHelperProvDataFromStateData(data.State);
            nint signerPointer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
            if (signerPointer == 0) throw new IOException("Podpis nemá ověřeného vydavatele.");
            var signer = Marshal.PtrToStructure<ProviderSigner>(signerPointer);
            if (signer.Count == 0 || signer.Certificates == 0) throw new IOException("Chybí certifikát podepisujícího.");
            // CRYPT_PROVIDER_CERT starts with cbStruct followed by the aligned PCCERT_CONTEXT.
            nint certificatePointer = Marshal.ReadIntPtr(signer.Certificates, 8);
            if (certificatePointer == 0) throw new IOException("Chybí podepisující certifikát.");
            var certificate = Marshal.PtrToStructure<CertificateContext>(certificatePointer);
            if (certificate.Length is 0 or > 65536 || certificate.Encoded == 0) throw new IOException("Neplatný podepisující certifikát.");
            var encoded = new byte[certificate.Length]; Marshal.Copy(certificate.Encoded, encoded, 0, encoded.Length);
            using var parsed = X509CertificateLoader.LoadCertificate(encoded);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(parsed.PublicKey.ExportSubjectPublicKeyInfo()), pin))
                throw new IOException("Vydavatel souboru neodpovídá připnutému veřejnému klíči.");
        } finally {
            data.Action = 2; WinVerifyTrust(0, ref policy, ref data); Marshal.FreeCoTaskMem(name);
        }
    }

    public void Dispose()
    {
        image?.Dispose(); image = null;
        foreach (var handle in directories) handle.Dispose(); directories.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct FileId(ulong Volume, ulong Low, ulong High);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributesInfo { internal uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile { internal uint Size; internal nint Path, Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData {
        internal uint Size; internal nint Callback, Sip; internal uint Ui, Revocation, Choice;
        internal nint File; internal uint Action; internal nint State, Url; internal uint Flags, Context; internal nint Signature;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderSigner { internal uint Size, TimeLow, TimeHigh, Count; internal nint Certificates; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext { internal uint Encoding; internal nint Encoded; internal uint Length; internal nint Info, Store; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileId(SafeFileHandle file, int kind, out FileId id, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetAttributes(SafeFileHandle file, int kind, out FileAttributesInfo attributes, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPath(SafeFileHandle file, char* text, uint size, uint flags);
    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveType(string root);
    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(SafeProcessHandle process, int kind, ref nint file, uint size, nint returned);
    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint owner, ref Guid policy, ref TrustData data);
    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperProvDataFromStateData(nint state);
    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvSignerFromChain(nint provider, uint signer, int counter, uint counterIndex);
}
