using System.Globalization;
using System.Reflection;
using System.Text;

namespace NksTaskMgr.WinUI;

/// <summary>UI text lookup keyed by the Czech source text; tables live in embedded Strings/&lt;language&gt;/*.tsv files.</summary>
internal static class Loc
{
    internal const string SystemPreference = "system";
    internal static readonly string[] Languages = ["cs", "en", "de", "sk", "pl"];
    private static readonly string preferencePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NksTaskMgr", "language.txt");
    private static readonly Dictionary<string, string> table;

    internal static string Preference { get; private set; }
    internal static string Language { get; }
    internal static CultureInfo Culture { get; }

    static Loc()
    {
        Preference = ReadPreference();
        Language = Preference == SystemPreference ? FromWindows(CultureInfo.CurrentUICulture) : Preference;
        Culture = CultureInfo.GetCultureInfo(Language switch { "en" => "en-US", "de" => "de-DE", "sk" => "sk-SK", "pl" => "pl-PL", _ => "cs-CZ" });
        table = Language == "cs" ? new(StringComparer.Ordinal) : Load(Language).Table;
    }

    /// <summary>Returns the translation of a Czech UI text, or the text itself when no translation exists.</summary>
    internal static string T(string text) => table.TryGetValue(text, out var value) ? value : text;

    private static readonly string[] nativeSuffixes = [" operací/s", " požadavků", " logických procesorů"];
    // Parts of longer native texts composed with numbers or names.
    private static readonly string[] nativeFragments = [" · publikovaný vzorek", " · zastaralé nebo nejistý čas", " · publikovaný čas je v budoucnu",
        " · stáří ", "Neznámý stav ", "Upozornění 0x", "Virtuální · "];

    /// <summary>Translates a fixed Czech value produced by the native collectors, or only its unit after a number.</summary>
    internal static string Native(string value)
    {
        if (table.TryGetValue(value, out var translated)) return translated;
        foreach (var suffix in nativeSuffixes)
            if (value.EndsWith(suffix, StringComparison.Ordinal)) return value[..^suffix.Length] + T(suffix);
        if (Language == "cs") return value;
        foreach (var fragment in nativeFragments)
            if (value.Contains(fragment, StringComparison.Ordinal)) value = value.Replace(fragment, T(fragment), StringComparison.Ordinal);
        return value;
    }

    /// <summary>Translates a composite format string and formats it with the UI culture.</summary>
    internal static string F(string format, params object?[] args) => string.Format(Culture, T(format), args);

    internal static string FromWindows(CultureInfo culture) => culture.TwoLetterISOLanguageName switch {
        "cs" => "cs", "sk" => "sk", "de" => "de", "pl" => "pl", _ => "en"
    };

    internal static string NativeName(string language) => language switch {
        "cs" => "Čeština", "en" => "English", "de" => "Deutsch", "sk" => "Slovenčina", "pl" => "Polski", _ => language
    };

    /// <summary>Saves the language for the next start; texts are created once per process.</summary>
    internal static bool Select(string preference)
    {
        if (preference != SystemPreference && !Languages.Contains(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        Preference = preference;
        string temporary = preferencePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(preferencePath)!);
            File.WriteAllText(temporary, preference, new UTF8Encoding(false));
            File.Move(temporary, preferencePath, true);
            return true;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        finally {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string ReadPreference()
    {
        try {
            string value = File.ReadAllText(preferencePath).Trim();
            if (value == SystemPreference || Languages.Contains(value)) return value;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            // An unreadable preference follows the Windows display language.
        }
        return SystemPreference;
    }

    /// <summary>Loads one language; conflicts lists keys translated differently in two files.</summary>
    internal static (Dictionary<string, string> Table, List<string> Conflicts) Load(string language)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        var assembly = typeof(Loc).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal)) {
            if (!name.StartsWith("NksStrings." + language + "\\", StringComparison.Ordinal) && !name.StartsWith("NksStrings." + language + "/", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line) {
                if (line.Length == 0 || line[0] == '#') continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0 || line.IndexOf('\t', tab + 1) >= 0) { conflicts.Add(name + ": malformed line " + line); continue; }
                string key = Unescape(line[..tab]), value = Unescape(line[(tab + 1)..]);
                if (result.TryGetValue(key, out var existing)) { if (existing != value) conflicts.Add(name + ": " + key); }
                else result.Add(key, value);
            }
        }
        return (result, conflicts);
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var text = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; ++i) {
            if (value[i] != '\\' || i + 1 == value.Length) { text.Append(value[i]); continue; }
            text.Append(value[++i] switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => value[i] });
        }
        return text.ToString();
    }
}
