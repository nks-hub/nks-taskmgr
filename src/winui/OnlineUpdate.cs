using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NksTaskMgr.WinUI;

/// <summary>A newer release published on GitHub with its setup asset.</summary>
internal sealed record OnlineRelease(Version Version, Uri SetupUrl, long Size, Uri Page);

/// <summary>How this copy was installed, which decides how its setup runs.</summary>
internal enum InstallKind { Machine, User, Portable }

/// <summary>
/// Finds the latest GitHub release, downloads its setup and starts it after the Authenticode signature matches the pinned
/// update publisher. Setup waits for this process to exit, installs in the same mode and starts the application again.
/// </summary>
internal static partial class OnlineUpdate
{
    private const string Repository = "nks-hub/nks-taskmgr";
    private const long MaximumSetupBytes = 256L * 1024 * 1024;
    private static readonly HttpClient http = CreateClient();

    internal static Uri ReleasesPage { get; } = new($"https://github.com/{Repository}/releases/latest");

    internal static InstallKind Kind {
        get {
            string root = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            if (File.Exists(Path.Combine(root, "portable.nks"))) return InstallKind.Portable;
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }) {
                string parent = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(folder));
                if (parent.Length > 0 && root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return InstallKind.Machine;
            }
            return InstallKind.User;
        }
    }

    internal static Version? CurrentVersion()
    {
        string? product = FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion;
        return product is null ? null : ParseVersion(product.Split('+')[0]);
    }

    /// <summary>The latest release when it is newer than <paramref name="current"/>, otherwise null.</summary>
    internal static async Task<OnlineRelease?> FindNewerAsync(Version current, CancellationToken cancellation)
    {
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellation);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var json = await JsonDocument.ParseAsync(stream, default, cancellation);
        return Parse(json.RootElement, current);
    }

    internal static OnlineRelease? Parse(JsonElement release, Version current)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        var version = tag.StartsWith('v') ? ParseVersion(tag[1..]) : null;
        if (version is null || version <= current) return null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray()) {
            var match = SetupName().Match(asset.GetProperty("name").GetString() ?? "");
            if (!match.Success || ParseVersion(match.Groups[1].Value) != version) continue;
            var url = new Uri(asset.GetProperty("browser_download_url").GetString() ?? "", UriKind.Absolute);
            if (url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" || !url.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException(Loc.T("Vydání odkazuje na instalátor mimo repozitář aplikace."));
            long size = asset.GetProperty("size").GetInt64();
            if (size is <= 0 or > MaximumSetupBytes) throw new InvalidDataException(Loc.T("Instalátor ve vydání má neplatnou velikost."));
            return new(version, url, size, new Uri(release.GetProperty("html_url").GetString() ?? ReleasesPage.ToString()));
        }
        throw new InvalidDataException(Loc.F("Vydání {0} nemá instalátor pro Windows x64.", tag));
    }

    /// <summary>Downloads the setup into the user's profile; the file is trusted only after <see cref="Start"/> verifies it.</summary>
    internal static async Task<string> DownloadAsync(OnlineRelease release, IProgress<double> progress, CancellationToken cancellation)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NksTaskMgr", "Downloads");
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder, "*.part")) try { File.Delete(old); } catch (IOException) { }
        string target = Path.Combine(folder, Path.GetFileName(release.SetupUrl.AbsolutePath)), partial = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try {
            using (var response = await http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancellation)) {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellation);
                await using var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920]; long total = 0;
                for (int read; (read = await source.ReadAsync(buffer, cancellation)) > 0;) {
                    total += read;
                    if (total > release.Size) throw new InvalidDataException(Loc.T("Stažený instalátor je větší, než vydání uvádí."));
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    progress.Report((double)total / release.Size);
                }
                if (total != release.Size) throw new InvalidDataException(Loc.T("Stažený instalátor není úplný."));
            }
            File.Move(partial, target, true);
            return target;
        } finally { try { File.Delete(partial); } catch (IOException) { } }
    }

    /// <summary>
    /// Verifies the held setup (signature chain, pinned publisher key, setup role and version) and starts it while the file stays
    /// locked against changes. Machine installs elevate through Setup's own UAC request. Returns false when the user declined.
    /// </summary>
    internal static bool Start(string setup, Version expected, InstallKind kind)
    {
        if (kind == InstallKind.Portable) throw new InvalidOperationException(Loc.T("Přenosnou kopii aktualizujte ručně z balíčku ZIP."));
        var pin = PublisherTrust.ReadPin(PublisherPolicy.Update)
            ?? throw new SignatureRejectedException(Loc.T("Toto sestavení nemá nastaveného důvěryhodného vydavatele aktualizací."));
        using var lease = SignedImageLease.Open(setup, pin);
        var product = PublisherTrust.RequireRole(lease, "NKS Task Manager Setup", "NksTaskMgrSetup.exe");
        if (ParseVersion(product.ProductVersion.Split('+')[0]) != expected)
            throw new SignatureRejectedException(Loc.F("Podepsaný instalátor má verzi {0}, očekávaná je {1}.", product.ProductVersion, expected));
        string mode = kind == InstallKind.Machine ? "/ALLUSERS" : "/CURRENTUSER";
        var start = new ProcessStartInfo(lease.LaunchPath,
            $"/SILENT /SUPPRESSMSGBOXES /NORESTART {mode} /NKSWAITPID={Environment.ProcessId} /NKSRELAUNCH=1") { UseShellExecute = false };
        try { Process.Start(start)?.Dispose(); return true; }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return false; }
    }

    internal static Version? ParseVersion(string text) =>
        VersionText().IsMatch(text) && Version.TryParse(text, out var value) ? value : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NKS-Task-Manager", CurrentVersion()?.ToString() ?? "0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    [GeneratedRegex(@"^\d{1,9}\.\d{1,9}\.\d{1,9}$")]
    private static partial Regex VersionText();
    [GeneratedRegex(@"^NksTaskMgr-(\d{1,9}\.\d{1,9}\.\d{1,9})-[0-9a-f]{7,40}-win-x64-setup\.exe$")]
    private static partial Regex SetupName();
}
