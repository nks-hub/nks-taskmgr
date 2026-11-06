using System.Text.RegularExpressions;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    /// <summary>Every translated language has the same keys, no conflicting duplicates and the same format placeholders.</summary>
    internal static void LocalizationChecks()
    {
        var english = Loc.Load("en");
        Require(english.Conflicts.Count == 0, "Conflicting English translations: " + string.Join("; ", english.Conflicts.Take(5)));
        Require(english.Table.Count > 0, "English translations are missing.");
        foreach (var language in Loc.Languages.Where(language => language is not ("cs" or "en"))) {
            var (table, conflicts) = Loc.Load(language);
            Require(conflicts.Count == 0, "Conflicting " + language + " translations: " + string.Join("; ", conflicts.Take(5)));
            var missing = english.Table.Keys.Where(key => !table.ContainsKey(key)).Take(5).ToArray();
            var extra = table.Keys.Where(key => !english.Table.ContainsKey(key)).Take(5).ToArray();
            Require(missing.Length == 0 && extra.Length == 0, language + " key set differs from English: missing " + string.Join(" | ", missing) + "; extra " + string.Join(" | ", extra));
            foreach (var (key, value) in table) CheckTranslation(language, key, value);
        }
        foreach (var (key, value) in english.Table) CheckTranslation("en", key, value);
        const string untranslated = "{0} · {1}";
        Require(Loc.F(untranslated, 1, "a") == "1 · a", "Format without a translation changed its text.");
    }

    private static void CheckTranslation(string language, string key, string value)
    {
        Require(value.Trim().Length > 0, language + " translation is empty: " + key);
        static string[] Holes(string text) => Placeholder().Matches(text).Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();
        Require(Holes(key).SequenceEqual(Holes(value)), language + " placeholders differ: " + key + " -> " + value);
    }

    [GeneratedRegex(@"\{\d+(?:,-?\d+)?(?::[^}]*)?\}")]
    private static partial Regex Placeholder();
}
