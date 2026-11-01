using System.Runtime.InteropServices;

namespace NksTaskMgr.WinUI;

internal sealed partial class GlobalObjectSearchPaths
{
    private readonly List<(string Dos, string Native)> drives = [];

    internal static unsafe GlobalObjectSearchPaths Capture(CancellationToken cancellation)
    {
        var paths = new GlobalObjectSearchPaths();
        var buffer = new char[32768];
        for (char letter = 'A'; letter <= 'Z'; ++letter) {
            cancellation.ThrowIfCancellationRequested();
            string drive = letter + ":";
            uint count;
            fixed (char* data = buffer) count = QueryDosDevice(drive, data, (uint)buffer.Length);
            if (count == 0 || count > buffer.Length) continue;
            int end = Array.IndexOf(buffer, '\0', 0, (int)count);
            // Only the first string is the current mapping; later strings are old mappings.
            if (end > 0) paths.drives.Add((drive, new string(buffer, 0, end)));
        }
        return paths;
    }

    internal string Canonical(string value)
    {
        value = Namespace(value);
        for (int pass = 0; pass <= drives.Count && IsDrivePath(value); ++pass) {
            var mapping = drives.Find(drive => value.StartsWith(drive.Dos, StringComparison.OrdinalIgnoreCase));
            if (mapping.Native is null) break;
            value = Namespace(mapping.Native.TrimEnd('\\') + value[2..]);
        }
        return value;
    }

    internal bool CanResolve(string query) => !IsDrivePath(Namespace(query)) || !IsDrivePath(Canonical(query));

    internal bool Matches(string observed, string query, string canonicalQuery) => query.Length == 0
        || observed.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Canonical(observed).Contains(canonicalQuery, StringComparison.OrdinalIgnoreCase);

    internal string DisplayName(string observed)
    {
        string native = Canonical(observed), result = observed;
        int longest = 0;
        foreach (var drive in drives) {
            string target = Canonical(drive.Native).TrimEnd('\\');
            if (target.Length <= longest || !Prefix(native, target)) continue;
            longest = target.Length; result = drive.Dos + native[target.Length..];
        }
        if (longest != 0) return result;
        return native.StartsWith(@"\Device\Mup\", StringComparison.OrdinalIgnoreCase) ? @"\\" + native[12..] : observed;
    }

    private static bool IsDrivePath(string value) => value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':'
        && (value.Length == 2 || value[2] == '\\');

    private static bool Prefix(string value, string prefix) => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (value.Length == prefix.Length || value[prefix.Length] == '\\');

    private static string Namespace(string value)
    {
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\.\", StringComparison.Ordinal)) value = value[4..];
        else if (value.StartsWith(@"\??\", StringComparison.Ordinal)) value = value[4..];
        else if (value.StartsWith(@"\DosDevices\", StringComparison.OrdinalIgnoreCase)) value = value[12..];
        if (value.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) return @"\Device\Mup\" + value[4..];
        if (value.StartsWith(@"\\", StringComparison.Ordinal)) return @"\Device\Mup" + value[1..];
        if (value.StartsWith(@"pipe\", StringComparison.OrdinalIgnoreCase)) return @"\Device\NamedPipe\" + value[5..];
        if (value.StartsWith(@"\Device\LanmanRedirector\", StringComparison.OrdinalIgnoreCase)) {
            string unc = value[25..];
            if (unc.StartsWith(';')) {
                int separator = unc.IndexOf('\\');
                if (separator < 0) return value;
                unc = unc[(separator + 1)..];
            }
            return @"\Device\Mup\" + unc;
        }
        return value;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint QueryDosDevice(string deviceName, char* targetPath, uint maximumCharacters);
}
