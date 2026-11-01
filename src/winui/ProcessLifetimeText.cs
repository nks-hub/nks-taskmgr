using System.Globalization;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static class ProcessLifetimeText
{
    internal static string Time(ProcessLifetimeObservation? observation) => observation is not { } sample ? "nezachyceno"
        : sample.Frequency <= 0 ? "čas nedostupný" : ((sample.Event.TimestampQpc - sample.Origin) / (double)sample.Frequency).ToString("+0.000;-0.000;0.000", CultureInfo.GetCultureInfo("cs-CZ")) + " s";
    internal static string State(ProcessLifetimeEntry entry)
    {
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Conflict)) return "Konfliktní záznam";
        if (entry.Key is null) return entry.Start is null ? "Neověřené ukončení" : "Neověřený vznik";
        if (entry.Paired) return entry.DurationSeconds is null ? "Čas nelze porovnat" : "Vznik i konec zachycen";
        if (entry.Start is null) return "Vznik nezachycen";
        return entry.CaptureState == ProcessEventMonitorState.Stopped ? "Bez konce při zastavení"
            : entry.Issues.HasFlag(ProcessLifetimeIssues.Boundary) ? "Oddělená část · bez konce" : "Konec zatím nezachycen";
    }
    internal static string Issues(ProcessLifetimeEntry entry)
    {
        string text = "";
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Gap)) text += " · proud s mezerou";
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Late)) text += " · opožděné pořadí";
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Conflict)) text += " · rozporné události";
        if (entry.Duplicates != 0) text += " · opakováno " + entry.Duplicates;
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Boundary)) text += " · oddělená část zdroje";
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Partial)) text += " · částečná událost";
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Trimmed)) text += " · starší údaje vytěsněny";
        return text;
    }
    internal static string Detail(ProcessLifetimeEntry entry)
    {
        string identity = entry.Key is { } key ? "Boot: " + key.Boot.High.ToString("X16", CultureInfo.InvariantCulture)
            + key.Boot.Low.ToString("X16", CultureInfo.InvariantCulture) + "\nPID " + key.Pid + " · " + key.Kind + " " + key.Generation
            : "Identita není ověřená. Událost se nepáruje podle PID, názvu ani blízkého času.";
        string duration = entry.DurationSeconds is { } seconds ? seconds.ToString("N6", CultureInfo.GetCultureInfo("cs-CZ")) + " s mezi skutečnými QPC událostmi."
            : "Délka není dostupná: chybí jednoznačný pár, společná platná frekvence QPC nebo správné časové pořadí.";
        return State(entry) + Issues(entry) + "\n" + identity + "\nZáznam " + entry.Capture + " · oddělená část " + entry.Segment
            + "\n" + Endpoint("Vznik", entry.Start) + "\n" + Endpoint("Konec", entry.Stop) + "\nDélka: " + duration
            + "\nStav zachytávání při sestavení řádku: " + entry.CaptureState
            + (entry.Issues.HasFlag(ProcessLifetimeIssues.ClockChanged) ? "\nHranice propojení: změnila se frekvence QPC." : "")
            + (entry.Issues.HasFlag(ProcessLifetimeIssues.BootChanged) ? "\nHranice propojení: změnil se identifikátor bootu." : "")
            + (entry.Issues.HasFlag(ProcessLifetimeIssues.SequenceReset) ? "\nHranice propojení: pořadí zdroje se opakovalo nebo vrátilo zpět." : "")
            + (entry.Issues.HasFlag(ProcessLifetimeIssues.CounterReset) ? "\nHranice propojení: čítače zdroje se vrátily zpět." : "")
            + "\nNezachycený konec neznamená, že proces právě běží. Osamocený konec neurčuje čas vzniku."
            + "\nZtráty nelze přesně umístit do intervalu. Vytěsněné řádky už nejsou k propojení; dřívější konflikt nelze vyloučit."
            + "\nCPU a RAM se zde neměří. Žádné živé akce ani vyhledání současného vlastníka stejného PID.";
    }
    private static string Endpoint(string name, ProcessLifetimeObservation? observation)
    {
        if (observation is not { } sample) return name + ": událost nezachycena.";
        var item = sample.Event;
        string text = name + ": " + Time(sample) + " · QPC " + item.TimestampQpc + " · frekvence " + sample.Frequency
            + "\nPořadí zdroje " + item.Sequence + " · přijato monotónně " + item.ObservedAtNs + " ns · příznaky " + item.Flags;
        if (item.Kind == ProcessEventKind.Stopped) text += item.Flags.HasFlag(ProcessEventFlags.HasExitStatus)
            ? "\nNávratový kód: 0x" + unchecked((uint)item.ExitStatus).ToString("X8", CultureInfo.InvariantCulture) : "\nNávratový kód není dostupný.";
        return text;
    }
}
