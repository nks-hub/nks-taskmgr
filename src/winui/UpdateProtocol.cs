using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal enum UpdateCommand : ushort { List = 1, Cache, Plan, Apply, Rollback, Commit }
internal enum UpdateFrame : uint { Request, Result, Ready, Accepted, Rejected }
internal enum UpdateStatus : uint { Ok, TrustMissing, Rejected, InvalidRequest, CacheFull, Conflict, NotFound, Io, GuiAlive, InstallerFailed, VerificationFailed, Unknown, Busy }
internal enum UpdateJournalState : uint { None, Cached, Planned, Committed, WaitingGui, Installing, Verified, Rejected, GuiAlive, InstallerFailed, VerificationFailed, Unknown }
internal enum UpdateAction : uint { Update = 1, Rollback }
[Flags]
internal enum UpdatePackageFlags : uint { Current = 1, Previous = 2 }
internal sealed record UpdatePackage(string Sha256, ulong Bytes, SignedImageLease.FileId FileId, uint Major, uint Minor, uint Patch,
    UpdatePackageFlags Flags, string Revision, DateTimeOffset CachedAt, string Path)
{
    internal string Version => Major + "." + Minor + "." + Patch + "+" + Revision;
}
internal sealed record UpdateInventory(UpdateStatus Status, uint Error, UpdateJournalState JournalState, string Detail, UpdatePackage[] Packages);
internal sealed record UpdatePlan(string PlanId, UpdateAction Action, uint Flags, DateTimeOffset CreatedAt, ulong ExpiresMonotonicMs,
    string PackageSha256, ulong PackageBytes, SignedImageLease.FileId PackageFileId, string InstalledAppSha256,
    SignedImageLease.FileId InstalledAppFileId, ulong InstalledAppBytes, string StageHelperSha256, SignedImageLease.FileId StageHelperFileId,
    ulong StageHelperBytes, ProcessKey Gui, ulong GuiCreated100ns, string InstallDirectory, string StageHelperPath,
    string SelectedVersion, string CurrentVersion, string PackagePath, string PreviousVersion);
internal sealed record UpdateHandoff(bool Accepted, bool MayHaveCommitted, UpdateStatus Status, uint Error, UpdateJournalState JournalState, string Detail);
internal sealed record UpdateResponse(UpdateFrame Frame, UpdateStatus Status, uint Error, UpdateJournalState JournalState,
    string Detail, ulong CommitDeadlineMonotonicMs, UpdatePackage[] Packages, UpdatePlan? Plan);
internal sealed class UpdateException(UpdateStatus status, string message, uint error = 0) : IOException(message)
{
    internal UpdateStatus Status { get; } = status;
    internal uint Error { get; } = error;
}
internal sealed class UpdatePeerUnresolvedException(string message, Exception error) : IOException(message, error);

internal static class UpdateProtocol
{
    internal const int HeaderBytes = 128, MaximumPayload = 65536;
    internal const ulong MaximumPackageBytes = 512ul * 1024 * 1024;
    internal static ulong NowMs => checked((ulong)Environment.TickCount64);
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);

    internal static byte[] Request(UpdateCommand command, ProcessKey gui, string? packageSha256 = null, byte[]? payload = null)
    {
        payload ??= [];
        if (gui.Boot.IsEmpty || gui.Pid == 0 || gui.Kind != GenerationKind.CreationTime || gui.Generation == 0 || payload.Length > MaximumPayload)
            throw new InvalidDataException("Klient aktualizací nemá ověřenou životnost.");
        var result = new byte[HeaderBytes + payload.Length];
        W32(result, 0, 0x3150554e); BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), (ushort)command); W32(result, 8, (uint)payload.Length);
        RandomNumberGenerator.Fill(result.AsSpan(16, 24));
        if (U64(result, 16) == 0) W64(result, 16, 1);
        if ((U64(result, 24) | U64(result, 32)) == 0) W64(result, 24, 1);
        W64(result, 40, checked(NowMs + 120000));
        W64(result, 48, gui.Boot.Low); W64(result, 56, gui.Boot.High); W32(result, 64, gui.Pid); W32(result, 68, (uint)gui.Kind);
        W64(result, 72, gui.Generation); W64(result, 80, gui.Generation);
        if (packageSha256 is not null) ParseHash(packageSha256).CopyTo(result, 88);
        payload.CopyTo(result, HeaderBytes); return result;
    }
    internal static byte[] CommitRequest(byte[] readyRequest)
    {
        var result = readyRequest.AsSpan(0, HeaderBytes).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), (ushort)UpdateCommand.Commit); W32(result, 8, 0); return result;
    }
    internal static byte[] CachePayload(SignedImageLease source, byte[] hash)
    {
        if (hash.Length != 32 || source.Length is 0 or > MaximumPackageBytes) throw new InvalidDataException("Neplatný balíček aktualizace.");
        ValidatePath(source.Path);
        var result = new byte[80 + source.Path.Length * 2];
        if (result.Length > MaximumPayload) throw new InvalidDataException("Cesta balíčku překračuje limit protokolu.");
        W64(result, 0, unchecked((ulong)source.Handle.DangerousGetHandle())); W64(result, 8, source.Length);
        W64(result, 16, source.Identity.Volume); W64(result, 24, source.Identity.Low); W64(result, 32, source.Identity.High);
        hash.CopyTo(result, 40); W32(result, 72, (uint)source.Path.Length); Utf16.GetBytes(source.Path, result.AsSpan(80)); return result;
    }
    internal static int PayloadLength(byte[] header, byte[] request)
    {
        if (header.Length != HeaderBytes || request.Length < HeaderBytes || !header.AsSpan(0, 8).SequenceEqual(request.AsSpan(0, 8))
            || !header.AsSpan(16).SequenceEqual(request.AsSpan(16, HeaderBytes - 16)) || U64(header, 120) != 0
            || U32(header, 12) is < 1 or > 4 || U32(header, 8) is < 40 or > MaximumPayload)
            throw new InvalidDataException("Odpověď aktualizací nepatří požadavku nebo ověřenému klientovi.");
        return checked((int)U32(header, 8));
    }
    internal static UpdateResponse Decode(byte[] header, byte[] payload, byte[] request)
    {
        if (payload.Length != PayloadLength(header, request)) throw new InvalidDataException("Neúplný rámec aktualizace.");
        var frame = (UpdateFrame)U32(header, 12); var status = (UpdateStatus)U32(payload, 0); var journal = (UpdateJournalState)U32(payload, 12);
        uint count = U32(payload, 8), planBytes = U32(payload, 20);
        ulong deadline = U64(payload, 24); int offset = 40;
        if (status > UpdateStatus.Busy || journal > UpdateJournalState.Unknown || count > 8 || U64(payload, 32) != 0
            || status == UpdateStatus.Ok && U32(payload, 4) != 0
            || planBytes > MaximumPayload || count != 0 && planBytes != 0 || frame != UpdateFrame.Ready && deadline != 0
            || frame == UpdateFrame.Rejected && (status == UpdateStatus.Ok || count != 0 || planBytes != 0)) throw Bad();
        string detail = Text(payload, ref offset, U32(payload, 16));
        var packages = new UpdatePackage[count]; var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < packages.Length; ++i) {
            if (offset > payload.Length - 144) throw Bad();
            int at = offset; offset += 144;
            string sha = Hash(payload, at), revision = Encoding.ASCII.GetString(payload, at + 80, 40);
            ulong size = U64(payload, at + 32); var id = Id(payload, at + 40);
            uint major = U32(payload, at + 64), minor = U32(payload, at + 68), patch = U32(payload, at + 72), flags = U32(payload, at + 76);
            if (!seen.Add(sha) || size is 0 or > MaximumPackageBytes || !ValidId(id) || (flags & ~3u) != 0 || !revision.All(Uri.IsHexDigit)
                || U32(payload, at + 132) != 0 || U64(payload, at + 136) != 0) throw Bad();
            string path = Text(payload, ref offset, U32(payload, at + 128)); ValidatePath(path);
            packages[i] = new(sha, size, id, major, minor, patch, (UpdatePackageFlags)flags, revision, Unix(U64(payload, at + 120)), path);
        }
        UpdatePlan? plan = null;
        if (planBytes != 0) {
            if (planBytes != payload.Length - offset || planBytes < 304) throw Bad();
            plan = Plan(payload.AsSpan(offset).ToArray(), request); offset = payload.Length;
        }
        if (offset != payload.Length || status != UpdateStatus.Ok && (count != 0 || plan is not null)
            || frame == UpdateFrame.Ready && (status != UpdateStatus.Ok || plan is null || deadline <= NowMs || deadline > NowMs + 30000)
            || frame == UpdateFrame.Accepted && (status != UpdateStatus.Ok || count != 0 || plan is not null)) throw Bad();
        return new(frame, status, U32(payload, 4), journal, detail, deadline, packages, plan);
    }
    private static UpdatePlan Plan(byte[] bytes, byte[] request)
    {
        var action = (UpdateAction)U32(bytes, 16); int at = 304;
        var gui = new ProcessKey(new(U64(bytes, 232), U64(bytes, 240)), U32(bytes, 248), (GenerationKind)U32(bytes, 252), U64(bytes, 256));
        var expected = new ProcessKey(new(U64(request, 48), U64(request, 56)), U32(request, 64), (GenerationKind)U32(request, 68), U64(request, 72));
        ulong expires = U64(bytes, 32);
        if (action is not (UpdateAction.Update or UpdateAction.Rollback) || U32(bytes, 20) != 3 || bytes.AsSpan(0, 16).IndexOfAnyExcept((byte)0) < 0
            || expires <= NowMs || expires > NowMs + 600000 || gui != expected || U64(bytes, 264) != gui.Generation || U64(bytes, 296) != 0) throw Bad();
        var texts = new string[6];
        for (int i = 0; i < texts.Length; ++i) texts[i] = Text(bytes, ref at, U32(bytes, 272 + i * 4));
        if (at != bytes.Length) throw Bad();
        foreach (int i in new[] { 0, 1, 4 }) ValidatePath(texts[i]);
        if (!PublisherTrust.ValidProductVersion(texts[2]) || !PublisherTrust.ValidProductVersion(texts[3])
            || !PublisherTrust.ValidProductVersion(texts[5]) || texts[5] != texts[3]
            || System.IO.Path.GetFileName(texts[1]) != "nks-update-worker.exe"
            || texts[1].StartsWith(texts[0].TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw Bad();
        var packageId = Id(bytes, 80); var installedId = Id(bytes, 136); var stageId = Id(bytes, 200);
        if (!ValidId(packageId) || !ValidId(installedId) || !ValidId(stageId) || U64(bytes, 72) is 0 or > MaximumPackageBytes
            || U64(bytes, 160) is 0 or > MaximumPackageBytes || U64(bytes, 224) is 0 or > MaximumPackageBytes) throw Bad();
        return new(Convert.ToHexString(bytes.AsSpan(0, 16)), action, 3, Unix(U64(bytes, 24)), expires, Hash(bytes, 40), U64(bytes, 72), packageId,
            Hash(bytes, 104), installedId, U64(bytes, 160), Hash(bytes, 168), stageId, U64(bytes, 224), gui, U64(bytes, 264),
            texts[0], texts[1], texts[2], texts[3], texts[4], texts[5]);
    }
    internal static byte[] ParseHash(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("Neplatný SHA256 balíčku.");
        var result = Convert.FromHexString(hash);
        if (result.AsSpan().IndexOfAnyExcept((byte)0) < 0) throw new ArgumentException("SHA256 balíčku nesmí být nulový.");
        return result;
    }
    internal static void ValidatePath(string path)
    {
        int root = path.IndexOf("}\\", StringComparison.Ordinal);
        if (!path.StartsWith("\\\\?\\Volume{", StringComparison.Ordinal) || root < 12 || path.Length is 0 or > 32767
            || path.Any(ch => ch < ' ') || path[(root + 2)..].Split('\\').Any(part => part is "." or ".." || part.Contains(':') || part.Contains('/')))
            throw new InvalidDataException("Pracovník nevrátil kanonickou místní cestu.");
    }
    private static string Text(byte[] bytes, ref int offset, uint chars)
    {
        if (chars > 32767 || offset > bytes.Length || chars * 2 > bytes.Length - offset) throw Bad();
        string text;
        try { text = Utf16.GetString(bytes.AsSpan(offset, checked((int)chars * 2))); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Neplatný text aktualizace.", error); }
        offset += checked((int)chars * 2);
        if (text.Any(ch => ch < ' ')) throw Bad();
        return text;
    }
    private static bool ValidId(SignedImageLease.FileId id) => id.Volume != 0 && (id.Low | id.High) != 0;
    private static SignedImageLease.FileId Id(byte[] bytes, int at) => new(U64(bytes, at), U64(bytes, at + 8), U64(bytes, at + 16));
    private static string Hash(byte[] bytes, int at) => bytes.AsSpan(at, 32).IndexOfAnyExcept((byte)0) >= 0 ? Convert.ToHexString(bytes.AsSpan(at, 32)) : throw Bad();
    private static DateTimeOffset Unix(ulong value) => value <= 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds((long)value) : throw Bad();
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static ulong U64(byte[] bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at));
    private static void W32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void W64(byte[] bytes, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
    private static InvalidDataException Bad() => new("Pracovník vrátil neplatná metadata aktualizace.");
    internal static string StatusText(UpdateStatus value) => value switch {
        UpdateStatus.Ok => "Potvrzeno", UpdateStatus.TrustMissing => "Chybí důvěra vydavatele", UpdateStatus.Rejected => "Požadavek odmítnut",
        UpdateStatus.InvalidRequest => "Neplatný požadavek", UpdateStatus.CacheFull => "Úložiště balíčků je plné", UpdateStatus.Conflict => "Souběžná operace nebo změněný plán",
        UpdateStatus.NotFound => "Balíček nebo předchozí ověřená instalace není dostupná", UpdateStatus.Io => "Chyba souboru nebo komunikace",
        UpdateStatus.GuiAlive => "Aplikace stále běží; instalace nebyla zahájena", UpdateStatus.InstallerFailed => "Instalátor ohlásil chybu",
        UpdateStatus.VerificationFailed => "Ověření instalace selhalo", UpdateStatus.Busy => "Jiná aktualizace probíhá", _ => "Výsledek není známý"
    };
}
