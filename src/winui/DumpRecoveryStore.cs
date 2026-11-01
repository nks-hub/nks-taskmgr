using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace NksTaskMgr.WinUI;

internal sealed record DumpRecoveryDocument(string Name, DumpRecoveryRecord? Record, string Proof, string Error);

internal static class DumpRecoveryStore
{
    private sealed class RecoveryLimitException(string message) : IOException(message) { }
    private const long MaximumTotal = 1024 * 1024, MaximumDocument = 512 * 1024;
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NksTaskMgr", "DumpRecovery");
    private static ProcessDumpFile DirectoryLease()
    {
        string app = Path.GetDirectoryName(Folder)!;
        using var local = ProcessDumpFile.HoldDirectory(Path.GetDirectoryName(app)!);
        Directory.CreateDirectory(app);
        using var appLease = ProcessDumpFile.HoldDirectory(app);
        var sid = new SecurityIdentifier(DumpRecoveryProcesses.UserSid);
        var security = new DirectorySecurity(); security.SetOwner(sid); security.SetAccessRuleProtection(true, false);
        foreach (var owner in new[] { sid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Folder).Create(security);
        var held = ProcessDumpFile.HoldDirectory(Folder);
        try {
            var actual = new DirectoryInfo(Folder).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            if (actual.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner.Value != sid.Value || !actual.AreAccessRulesProtected)
                throw new IOException("Soukromé úložiště obnovy nemá ověřeného vlastníka nebo ochranu přístupu.");
            foreach (FileSystemAccessRule rule in actual.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference.Value != sid.Value && rule.IdentityReference.Value != "S-1-5-18")
                    throw new IOException("Úložiště obnovy má neočekávaná oprávnění. Záznamy zůstávají zachované.");
            return held;
        } catch { held.Dispose(); throw; }
    }
    private static SafeFileHandle Lock(ProcessDumpFile directory)
    {
        var file = ProcessDumpFile.OpenEntry(directory.Parent, ".lock", 0xc0110000, 0, 3);
        try {
            ProcessDumpFile.CheckSingleFile(file);
            if (ProcessDumpFile.Owner(file) != DumpRecoveryProcesses.UserSid || RandomAccess.GetLength(file) != 0)
                throw new IOException("Zámek úložiště obnovy není ověřitelný.");
            return file;
        } catch { file.Dispose(); throw; }
    }
    private static string[] Names()
    {
        var names = Directory.EnumerateFileSystemEntries(Folder, "*", SearchOption.TopDirectoryOnly).Take(130)
            .Select(path => Path.GetFileName(path) ?? throw new IOException("Neplatný název záznamu obnovy.")).ToArray();
        if (names.Length > 129) throw new IOException("Úložiště obnovy překročilo omezený počet souborů. Nic se nebude měnit.");
        return names;
    }
    private static bool Canonical(string name) => name.Length == 37 && name.EndsWith(".json", StringComparison.Ordinal)
        && Guid.TryParseExact(name[..32], "N", out var id) && name == id.ToString("N") + ".json";
    private static byte[] Read(ProcessDumpFile directory, string name, long maximumBytes = MaximumDocument)
    {
        using var file = ProcessDumpFile.OpenEntry(directory.Parent, name);
        ProcessDumpFile.CheckSingleFile(file);
        if (ProcessDumpFile.Owner(file) != DumpRecoveryProcesses.UserSid) throw new IOException("Vlastník záznamu obnovy nesouhlasí.");
        long length = RandomAccess.GetLength(file);
        if (length is <= 0 or > MaximumDocument) throw new IOException("Záznam obnovy má nepovolenou velikost.");
        if (length > maximumBytes) throw new RecoveryLimitException("Úložiště překročilo limit 1 MiB. Nic se nebude měnit.");
        var bytes = new byte[length]; int at = 0;
        while (at < bytes.Length) { int read = RandomAccess.Read(file, bytes.AsSpan(at), at); if (read == 0) throw new IOException("Záznam obnovy je neúplný."); at += read; }
        return bytes;
    }
    private static DumpRecoveryDocument Decode(string name, byte[] bytes)
    {
        var value = JsonSerializer.Deserialize(bytes, DumpRecoveryJsonContext.Default.DumpRecoveryRecord) ?? throw new IOException("Záznam obnovy je prázdný.");
        Validate(value, name);
        return new(name, value, Convert.ToHexString(SHA256.HashData(bytes)), "");
    }
    internal static void Validate(DumpRecoveryRecord value, string name)
    {
        static bool Creator(DumpCreator? identity) => identity is not null && !identity.Boot.IsEmpty && identity.Pid != 0 && identity.Created100ns != 0;
        static bool FileId(DumpFileIdentity id) => id.Volume != 0 && (id.Low != 0 || id.High != 0);
        static bool PathText(string? text) => text is { Length: > 7 and <= 32760 } && text.StartsWith("\\\\?\\", StringComparison.Ordinal)
            && char.IsAsciiLetter(text[4]) && text[5] == ':' && text[6] == '\\' && !text.Contains('\0')
            && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(text[4..]), text[4..]);
        if (value.Version != 1 || !Canonical(name) || value.Id != name[..32] || value.OwnerSid != DumpRecoveryProcesses.UserSid || value.Started == default || value.FileOwnerSid is null
            || (uint)value.Phase > (uint)DumpRecoveryPhase.Ready || !Creator(value.Creator)
            || value.Target is null || value.Target.Key.Boot != value.Creator.Boot || value.Target.Key.Pid == 0 || value.Target.Key.Generation == 0 || value.Target.Created100ns == 0
            || value.Target.Key.Kind is not (NksTaskMgr.Native.GenerationKind.CreationTime or NksTaskMgr.Native.GenerationKind.SequenceNumber)
            || value.Mode is not (ProcessDumpMode.Mini or ProcessDumpMode.Full) || !PathText(value.Destination) || !PathText(value.Temporary)
            || !FileId(value.ParentId) || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(value.Destination), Path.GetDirectoryName(value.Temporary))
            || Path.GetFileName(value.Temporary) != ".nks-dump-" + value.Id + ".tmp" || StringComparer.OrdinalIgnoreCase.Equals(value.Destination, value.Temporary)
            || value.Phase != DumpRecoveryPhase.Intent && (!FileId(value.FileId) || string.IsNullOrEmpty(value.FileOwnerSid))
            || value.Helper is not null && (!Creator(value.Helper) || value.Helper.Boot != value.Creator.Boot)
            || (uint)value.Phase < (uint)DumpRecoveryPhase.WorkerReady && (value.Helper is not null || value.RequestId != 0 || value.NonceLow != 0 || value.NonceHigh != 0)
            || (uint)value.Phase >= (uint)DumpRecoveryPhase.WorkerReady && (!Creator(value.Helper) || value.RequestId == 0 || (value.NonceLow == 0 && value.NonceHigh == 0))
            || value.Phase == DumpRecoveryPhase.Ready && (value.FileBytes is 0 or > ProcessDumpProtocol.MaximumFileBytes || value.Sha256 is not { Length: 64 }
                || value.Sha256.Any(c => !Uri.IsHexDigit(c)) || value.Architecture is not (ProcessArchitecture.X86 or ProcessArchitecture.X64)
                || value.CaptureFlags != (value.Mode == ProcessDumpMode.Full ? 0x1802u : 0)))
            throw new IOException("Záznam obnovy je poškozený nebo neodpovídá podporované transakci. Zůstane zachovaný.");
        try { _ = new SecurityIdentifier(value.OwnerSid); if (value.Phase != DumpRecoveryPhase.Intent) _ = new SecurityIdentifier(value.FileOwnerSid); }
        catch (ArgumentException error) { throw new IOException("Identita vlastníka v záznamu je neplatná.", error); }
    }
    internal static DumpRecoveryDocument[] ReadAll(CancellationToken cancellation)
    {
        using var directory = DirectoryLease(); using var held = Lock(directory);
        var names = Names(); var result = new List<DumpRecoveryDocument>(); long total = 0; int journals = 0;
        foreach (string name in names.Where(name => name != ".lock")) {
            cancellation.ThrowIfCancellationRequested();
            if (!Canonical(name)) { result.Add(new(name, null, "", "Neznámý nebo nedokončený pomocný zápis zůstane zachovaný.")); continue; }
            if (++journals > 64) throw new IOException("Úložiště překročilo limit 64 záznamů. Nic se nebude měnit.");
            try {
                var bytes = Read(directory, name, MaximumTotal - total); total += bytes.Length;
                result.Add(Decode(name, bytes));
            } catch (Exception error) when (error is not RecoveryLimitException && (error is IOException or JsonException or Win32Exception or ArgumentException or UnauthorizedAccessException)) {
                result.Add(new(name, null, "", "Záznam nelze bezpečně přečíst. Zůstane zachovaný. " + error.Message));
            }
        }
        return result.ToArray();
    }
    internal static DumpRecoveryDocument Reload(string id, string expectedProof)
    {
        using var directory = DirectoryLease(); using var held = Lock(directory);
        var result = Decode(id + ".json", Read(directory, id + ".json"));
        if (result.Proof != expectedProof) throw new IOException("Záznam obnovy se změnil. Obnovte přehled; soubor zůstal zachovaný.");
        return result;
    }
    internal static string Write(DumpRecoveryRecord record, string? previousProof)
    {
        string name = record.Id + ".json"; Validate(record, name);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, DumpRecoveryJsonContext.Default.DumpRecoveryRecord);
        if (bytes.Length > MaximumDocument) throw new IOException("Záznam dumpu překračuje limit bezpečného úložiště.");
        string proof = Convert.ToHexString(SHA256.HashData(bytes));
        using var directory = DirectoryLease(); using var held = Lock(directory);
        string[] names = Names(); long total = 0;
        if (names.Count(Canonical) >= 64 && previousProof is null) throw new IOException("Úložiště obsahuje 64 nedokončených dumpů. Nejdřív otevřete jejich přehled.");
        foreach (string item in names.Where(item => item != ".lock")) {
            using var existing = ProcessDumpFile.OpenEntry(directory.Parent, item);
            ProcessDumpFile.CheckSingleFile(existing); total += RandomAccess.GetLength(existing);
            if (total + bytes.Length > MaximumTotal) throw new IOException("Úložiště obnovy překročilo limit 1 MiB. Nový dump nebyl zahájen.");
        }
        if (previousProof is not null) {
            var old = Read(directory, name); _ = Decode(name, old);
            if (Convert.ToHexString(SHA256.HashData(old)) != previousProof) throw new IOException("Trvalý záznam dumpu se změnil. Nebude přepsán.");
        } else if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new IOException("Identifikátor transakce už existuje.");
        string temporary = "." + record.Id + "-" + Guid.NewGuid().ToString("N") + ".tmp";
        using var file = ProcessDumpFile.OpenEntry(directory.Parent, temporary, 0xc0110000, 0, 2); bool moved = false;
        try {
            RandomAccess.Write(file, bytes, 0); RandomAccess.FlushToDisk(file);
            ProcessDumpFile.RenameEntry(file, directory.Parent, name, previousProof is not null); moved = true;
            return proof;
        } finally { if (!moved) ProcessDumpFile.DeleteEntry(file); }
    }
    internal static void Remove(string id, string proof)
    {
        using var directory = DirectoryLease(); using var held = Lock(directory);
        string name = id + ".json";
        using var file = ProcessDumpFile.OpenEntry(directory.Parent, name, 0x80110000, 0);
        ProcessDumpFile.CheckSingleFile(file);
        if (ProcessDumpFile.Owner(file) != DumpRecoveryProcesses.UserSid) throw new IOException("Vlastník záznamu se změnil. Záznam zůstane zachovaný.");
        long length = RandomAccess.GetLength(file);
        if (length is <= 0 or > MaximumDocument) throw new IOException("Záznam obnovy je poškozený. Zůstane zachovaný.");
        var bytes = new byte[length]; int at = 0;
        while (at < bytes.Length) { int read = RandomAccess.Read(file, bytes.AsSpan(at), at); if (read == 0) throw new IOException("Záznam je neúplný."); at += read; }
        _ = Decode(name, bytes);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != proof) throw new IOException("Záznam obnovy se změnil. Zůstane zachovaný.");
        ProcessDumpFile.DeleteEntry(file);
    }
}
