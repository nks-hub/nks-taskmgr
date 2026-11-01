using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed class ElevationBrokerTrustException(uint error, string message) : IOException(message)
{
    internal uint Error { get; } = error;
}

internal sealed unsafe partial class ElevationBrokerTrust : IDisposable
{
    private readonly List<SafeFileHandle> directories = [];
    private SafeFileHandle? image;
    private FileId id;
    internal string Path { get; private set; } = "";

    internal static ElevationBrokerTrust Open()
    {
        string? pin = typeof(ElevationBrokerTrust).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(value => value.Key == "Nks.BrokerPublisherSpkiSha256")?.Value;
        if (pin is null || pin.Length != 64 || !pin.All(Uri.IsHexDigit))
            throw new ElevationBrokerTrustException(0xe04e0001, ElevationBrokerClient.Status(0xe04e0001));
        var lease = new ElevationBrokerTrust();
        try {
            lease.image = CreateFile(System.IO.Path.Combine(AppContext.BaseDirectory, "nks-elevation-broker.exe"), 0x80100020, 1, 0, 3, 0, 0);
            if (lease.image.IsInvalid || !GetFileId(lease.image, 18, out lease.id, (uint)sizeof(FileId))) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var buffer = new char[32768]; uint length;
            fixed (char* name = buffer) length = GetFinalPath(lease.image, name, (uint)buffer.Length, 1);
            if (length == 0 || length >= buffer.Length) throw new Win32Exception(Marshal.GetLastPInvokeError());
            lease.Path = new string(buffer, 0, checked((int)length));
            int rootEnd = lease.Path.IndexOf("}\\", StringComparison.Ordinal) + 1;
            if (!lease.Path.StartsWith("\\\\?\\Volume{", StringComparison.Ordinal) || rootEnd < 1 || GetDriveType(lease.Path[..(rootEnd + 1)]) != 3)
                throw new IOException("Broker musí být na ověřeném místním pevném svazku.");
            for (int slash = rootEnd; slash >= 0; slash = lease.Path.IndexOf('\\', slash + 1)) {
                string directory = lease.Path[..(slash == rootEnd ? slash + 1 : slash)];
                var handle = CreateFile(directory, 0xa0, 1, 0, 3, 0x02200000, 0);
                lease.directories.Add(handle);
                if (handle.IsInvalid || !GetAttributes(handle, 9, out var attributes, 8) || (attributes.Attributes & 0x410) != 0x10)
                    throw new IOException("Cestu brokeru nelze zamknout bez změnitelných odkazů.");
            }
            using var reopened = CreateFile(lease.Path, 0x80100020, 1, 0, 3, 0, 0);
            if (reopened.IsInvalid || !GetFileId(reopened, 18, out var actual, (uint)sizeof(FileId)) || actual != lease.id)
                throw new IOException("Soubor brokeru se při ověření změnil.");
            lease.CheckSignature(Convert.FromHexString(pin)); return lease;
        } catch (Exception error) when (error is IOException or Win32Exception or CryptographicException) {
            lease.Dispose(); throw new ElevationBrokerTrustException(0xe04e0002, "Broker nebyl spuštěn: " + error.Message);
        }
    }

    internal void VerifyProcess(Process process)
    {
        nint file = image!.DangerousGetHandle();
        ProcessIdentityInfo key;
        try { key = NativeMonitor.QueryProcessIdentity(process.SafeHandle, GenerationKind.CreationTime); }
        catch (NativeMonitorException error) { throw new ElevationBrokerTrustException(0xe04e0002, "Identitu spuštěného brokeru nelze ověřit: " + error.Message); }
        if (key.State != SampleState.Valid || key.Boot.IsEmpty || key.Generation == 0 || key.Pid != process.Id
            || NtQueryInformationProcess(process.SafeHandle, 44, ref file, (uint)sizeof(nint), 0) != 0
            || WaitForSingleObject(process.SafeHandle, 0) != 258
            || !GetFileId(image, 18, out var after, (uint)sizeof(FileId)) || after != id)
            throw new ElevationBrokerTrustException(0xe04e0002, "Spuštěný proces neodpovídá drženému podepsanému souboru brokeru. Požadavek nebyl odeslán.");
    }

    private void CheckSignature(byte[] pin)
    {
        nint name = Marshal.StringToCoTaskMemUni(Path);
        var file = new TrustFile { Size = (uint)sizeof(TrustFile), Path = name, Handle = image!.DangerousGetHandle() };
        var data = new TrustData { Size = (uint)sizeof(TrustData), Ui = 2, Revocation = 1, Choice = 1, File = (nint)(&file), Action = 1, Flags = 0x1080 };
        var policy = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try {
            if (WinVerifyTrust(0, ref policy, ref data) != 0) throw new IOException("Podpis brokeru nebo řetězec důvěry není platný.");
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
                throw new IOException("Vydavatel brokeru neodpovídá připnutému veřejnému klíči.");
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
    private record struct FileId(ulong Volume, ulong Low, ulong High);
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
