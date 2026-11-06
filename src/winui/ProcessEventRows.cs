using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class ProcessEventRow
{
    private readonly long frequency;
    private readonly BootIdentifier boot;
    internal ProcessEventRow(ProcessEventInfo item, long frequency, long origin, BootIdentifier boot, SampleState bootState)
    {
        Source = item; this.frequency = frequency; this.boot = boot;
        KindText = item.Kind == ProcessEventKind.Started ? Loc.T("Vznik") : Loc.T("Ukončení");
        double elapsed = frequency > 0 ? (item.TimestampQpc - origin) / (double)frequency : 0;
        TimeText = frequency > 0 ? (elapsed >= 0 ? "+" : "") + elapsed.ToString("N3", Loc.Culture) + " s" : Loc.T("Čas ETW nedostupný");
        NameText = string.IsNullOrEmpty(item.ImageName) ? Loc.T("Název není dostupný") : GlobalObjectSearchText.Flat(item.ImageName, 1024);
        TargetText = Loc.F("PID {0} · rodič {1}", item.Pid, ((uint)item.Flags & 8) != 0 ? item.ParentPid.ToString(CultureInfo.InvariantCulture) : "?");
        bool verified = bootState == SampleState.Valid && !boot.IsEmpty && item.Pid != 0
            && item.IdentityState == SampleState.Valid && item.IdentityKind == GenerationKind.CreationTime && item.Generation != 0;
        IdentityText = verified ? Loc.T("CreateTime přímo ze zdroje · ověřená historická životnost") : Loc.T("Historický záznam · životnost není ověřená");
        EstimatedBytes = 512 + 2 * (item.ImageName.Length + NameText.Length + TargetText.Length + TimeText.Length + KindText.Length + IdentityText.Length);
    }
    internal ProcessEventInfo Source { get; }
    internal int EstimatedBytes { get; }
    internal bool Matches(string query) => NameText.Contains(query, StringComparison.OrdinalIgnoreCase)
        || TargetText.Contains(query, StringComparison.OrdinalIgnoreCase) || KindText.Contains(query, StringComparison.OrdinalIgnoreCase)
        || IdentityText.Contains(query, StringComparison.OrdinalIgnoreCase);
    internal string Detail => Lifetime is { } lifetime ? ProcessLifetimeText.Detail(lifetime) : Traffic is not null ? TrafficDetail : KindText + " · " + NameText + "\n" + TargetText + "\n" + IdentityText + "\n"
        + Loc.F("Pořadí ve zdroji: {0}\nČas ETW (QPC): {1}\nFrekvence QPC: {2}\nPřijato (monotónní ns): {3}\nStav identity: {4}\nDruh generace: {5} · generace {6}\nPříznaky události: {7}",
            Source.Sequence, Source.TimestampQpc, frequency, Source.ObservedAtNs, Source.IdentityState, Source.IdentityKind, Source.Generation, Source.Flags)
        + "\nBoot: " + boot.High.ToString("X16", CultureInfo.InvariantCulture) + boot.Low.ToString("X16", CultureInfo.InvariantCulture)
        + (((uint)Source.Flags & 1) != 0 ? Loc.F("\nNávratový kód: 0x{0}", unchecked((uint)Source.ExitStatus).ToString("X8", CultureInfo.InvariantCulture)) : Loc.T("\nNávratový kód není dostupný."))
        + Loc.T("\nTento záznam neopravňuje k živé akci nad současným vlastníkem stejného PID.");
    public string TimeText { get; }
    public string KindText { get; }
    public string NameText { get; }
    public string TargetText { get; }
    public string IdentityText { get; }
    public override string ToString() => KindText + ", " + NameText + ", " + TargetText + ", " + TimeText;
}

[WinRT.GeneratedWinRTExposedType]
public sealed partial class ProcessEventRows : ObservableCollection<ProcessEventRow>
{
    internal void Apply(IReadOnlyList<ProcessEventRow> values)
    {
        if (Count == values.Count && this.SequenceEqual(values)) return;
        Items.Clear();
        foreach (var value in values) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
