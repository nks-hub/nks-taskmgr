using System.Globalization;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static class ProcessLifetimeText
{
    internal static string Time(ProcessLifetimeObservation? observation) => observation is not { } sample ? Loc.T("nezachyceno")
        : sample.Frequency <= 0 ? Loc.T("čas nedostupný") : ((sample.Event.TimestampQpc - sample.Origin) / (double)sample.Frequency).ToString("+0.000;-0.000;0.000", Loc.Culture) + " s";
    internal static string State(ProcessLifetimeEntry entry)
    {
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Conflict)) return Loc.T("Konfliktní záznam");
        if (entry.Key is null) return entry.Start is null ? Loc.T("Neověřené ukončení") : Loc.T("Neověřený vznik");
        if (entry.Paired) return entry.DurationSeconds is null ? Loc.T("Čas nelze porovnat") : Loc.T("Vznik i konec zachycen");
        if (entry.Start is null) return Loc.T("Vznik nezachycen");
        return entry.CaptureState == ProcessEventMonitorState.Stopped ? Loc.T("Bez konce při zastavení")
            : entry.Issues.HasFlag(ProcessLifetimeIssues.Boundary) ? Loc.T("Oddělená část · bez konce") : Loc.T("Konec zatím nezachycen");
    }
    internal static string Issues(ProcessLifetimeEntry entry)
    {
        string text = "";
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Gap)) text += " · " + Loc.T("proud s mezerou");
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Late)) text += " · " + Loc.T("opožděné pořadí");
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Conflict)) text += " · " + Loc.T("rozporné události");
        if (entry.Duplicates != 0) text += " · " + Loc.F("opakováno {0}", entry.Duplicates);
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Boundary)) text += " · " + Loc.T("oddělená část zdroje");
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Partial)) text += " · " + Loc.T("částečná událost");
        if (entry.Issues.HasFlag(ProcessLifetimeIssues.Trimmed)) text += " · " + Loc.T("starší údaje vytěsněny");
        return text;
    }
    internal static string Detail(ProcessLifetimeEntry entry)
    {
        string identity = entry.Key is { } key ? "Boot: " + key.Boot.High.ToString("X16", CultureInfo.InvariantCulture)
            + key.Boot.Low.ToString("X16", CultureInfo.InvariantCulture) + "\nPID " + key.Pid + " · " + key.Kind + " " + key.Generation
            : Loc.T("Identita není ověřená. Událost se nepáruje podle PID, názvu ani blízkého času.");
        string duration = entry.DurationSeconds is { } seconds ? Loc.F("{0} s mezi skutečnými QPC událostmi.", seconds.ToString("N6", Loc.Culture))
            : Loc.T("Délka není dostupná: chybí jednoznačný pár, společná platná frekvence QPC nebo správné časové pořadí.");
        return State(entry) + Issues(entry) + "\n" + identity + "\n" + Loc.F("Záznam {0} · oddělená část {1}", entry.Capture, entry.Segment)
            + "\n" + Endpoint(Loc.T("Vznik"), entry.Start) + "\n" + Endpoint(Loc.T("Konec"), entry.Stop) + "\n" + Loc.F("Délka: {0}", duration)
            + "\n" + Loc.F("Stav zachytávání při sestavení řádku: {0}", entry.CaptureState)
            + (entry.Issues.HasFlag(ProcessLifetimeIssues.ClockChanged) ? "\n" + Loc.T("Hranice propojení: změnila se frekvence QPC.") : "")
            + (entry.Issues.HasFlag(ProcessLifetimeIssues.BootChanged) ? "\n" + Loc.T("Hranice propojení: změnil se identifikátor bootu.") : "")
            + (entry.Issues.HasFlag(ProcessLifetimeIssues.SequenceReset) ? "\n" + Loc.T("Hranice propojení: pořadí zdroje se opakovalo nebo vrátilo zpět.") : "")
            + (entry.Issues.HasFlag(ProcessLifetimeIssues.CounterReset) ? "\n" + Loc.T("Hranice propojení: čítače zdroje se vrátily zpět.") : "")
            + "\n" + Loc.T("Nezachycený konec neznamená, že proces právě běží. Osamocený konec neurčuje čas vzniku.")
            + "\n" + Loc.T("Ztráty nelze přesně umístit do intervalu. Vytěsněné řádky už nejsou k propojení; dřívější konflikt nelze vyloučit.")
            + "\n" + Loc.T("CPU a RAM se zde neměří. Žádné živé akce ani vyhledání současného vlastníka stejného PID.");
    }
    private static string Endpoint(string name, ProcessLifetimeObservation? observation)
    {
        if (observation is not { } sample) return Loc.F("{0}: událost nezachycena.", name);
        var item = sample.Event;
        string text = Loc.F("{0}: {1} · QPC {2} · frekvence {3}", name, Time(sample), item.TimestampQpc, sample.Frequency)
            + "\n" + Loc.F("Pořadí zdroje {0} · přijato monotónně {1} ns · příznaky {2}", item.Sequence, item.ObservedAtNs, item.Flags);
        if (item.Kind == ProcessEventKind.Stopped) text += item.Flags.HasFlag(ProcessEventFlags.HasExitStatus)
            ? "\n" + Loc.F("Návratový kód: 0x{0}", unchecked((uint)item.ExitStatus).ToString("X8", CultureInfo.InvariantCulture)) : "\n" + Loc.T("Návratový kód není dostupný.");
        return text;
    }
}
