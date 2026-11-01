using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed class ProcessEventRow
{
    private readonly long frequency;
    internal ProcessEventRow(ProcessEventInfo item, long frequency, long origin)
    {
        Source = item; this.frequency = frequency;
        KindText = item.Kind == ProcessEventKind.Started ? "Vznik" : "Ukončení";
        double elapsed = frequency > 0 ? (item.TimestampQpc - origin) / (double)frequency : 0;
        TimeText = frequency > 0 ? (elapsed >= 0 ? "+" : "") + elapsed.ToString("N3", CultureInfo.GetCultureInfo("cs-CZ")) + " s" : "Čas ETW nedostupný";
        NameText = string.IsNullOrEmpty(item.ImageName) ? "Název není dostupný" : GlobalObjectSearchText.Flat(item.ImageName, 1024);
        TargetText = "PID " + item.Pid + " · rodič " + (((uint)item.Flags & 8) != 0 ? item.ParentPid.ToString(CultureInfo.InvariantCulture) : "?");
        IdentityText = "Historický záznam · životnost není ověřená";
        EstimatedBytes = 512 + 2 * (item.ImageName.Length + NameText.Length + TargetText.Length + TimeText.Length + KindText.Length + IdentityText.Length);
    }
    internal ProcessEventInfo Source { get; }
    internal int EstimatedBytes { get; }
    internal bool Matches(string query) => NameText.Contains(query, StringComparison.OrdinalIgnoreCase)
        || TargetText.Contains(query, StringComparison.OrdinalIgnoreCase) || KindText.Contains(query, StringComparison.OrdinalIgnoreCase);
    internal string Detail => KindText + " · " + NameText + "\n" + TargetText + "\n"
        + "Pořadí ve zdroji: " + Source.Sequence + "\nČas ETW (QPC): " + Source.TimestampQpc + "\nFrekvence QPC: " + frequency
        + "\nPřijato (monotónní ns): " + Source.ObservedAtNs + "\nStav identity: " + Source.IdentityState
        + "\nDruh generace: " + Source.IdentityKind + " · generace " + Source.Generation + "\nPříznaky události: " + Source.Flags
        + (((uint)Source.Flags & 1) != 0 ? "\nNávratový kód: 0x" + unchecked((uint)Source.ExitStatus).ToString("X8", CultureInfo.InvariantCulture) : "\nNávratový kód není dostupný.")
        + "\nTento záznam neopravňuje k živé akci nad současným vlastníkem stejného PID.";
    public string TimeText { get; }
    public string KindText { get; }
    public string NameText { get; }
    public string TargetText { get; }
    public string IdentityText { get; }
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
