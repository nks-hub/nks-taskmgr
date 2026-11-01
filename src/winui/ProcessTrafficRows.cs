using System.Globalization;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class ProcessEventRow
{
    internal ProcessTrafficSample? Traffic { get; }
    internal ProcessEventRow(ProcessTrafficSample item, long frequency, long origin, BootIdentifier boot)
    {
        Traffic = item; this.frequency = frequency; this.boot = boot;
        string source = item.Source switch { ProcessTrafficSource.PhysicalDisk => "Disk", ProcessTrafficSource.Tcp => "TCP", _ => "UDP" };
        KindText = source + (item.Direction switch { ProcessTrafficDirection.Read => " čt.", ProcessTrafficDirection.Write => " záp.", ProcessTrafficDirection.Sent => " ↑", _ => " ↓" });
        double elapsed = frequency > 0 ? (item.StartQpc - origin) / (double)frequency : 0;
        TimeText = frequency > 0 ? (elapsed >= 0 ? "+" : "") + elapsed.ToString("N3", Czech) + " s" : "Čas ETW nedostupný";
        NameText = item.ObservedBytes.ToString("N0", Czech) + " B pozorováno · " + item.Operations.ToString("N0", Czech)
            + " událostí · " + item.ObservedBytesPerSecond.ToString("N1", Czech) + " B/s v okně 1 s";
        TargetText = (item.Pid == 0 ? "PID nelze určit" : "PID " + item.Pid.ToString(CultureInfo.InvariantCulture))
            + " · " + source + (item.Source != ProcessTrafficSource.PhysicalDisk ? (item.Flags.HasFlag(ProcessTrafficFlags.Ipv6) ? " IPv6" : " IPv4") : " · fyzické I/O");
        IdentityText = item.Attribution switch {
            ProcessTrafficAttribution.Correlated => "Částečné · korelace PID/času · životnost nepotvrzená",
            ProcessTrafficAttribution.Direct => "Pozorovaná podmnožina · přímá historická životnost",
            _ => "Částečné · nelze přiřadit historické životnosti"
        };
        if (item.Flags.HasFlag(ProcessTrafficFlags.IdentityConflict)) IdentityText += " · dřívější přiřazení odvoláno";
        if (item.Flags.HasFlag(ProcessTrafficFlags.CounterOverflow)) IdentityText += " · čítač přetekl";
        if (item.Flags.HasFlag(ProcessTrafficFlags.WindowOpen)) IdentityText += " · okno není uzavřené";
        EstimatedBytes = 1024 + 2 * (NameText.Length + TargetText.Length + TimeText.Length + KindText.Length + IdentityText.Length);
    }
    private static CultureInfo Czech => CultureInfo.GetCultureInfo("cs-CZ");
    private string TrafficDetail {
        get {
            var item = Traffic!.Value;
            string attribution = item.Attribution switch {
                ProcessTrafficAttribution.Correlated => "Kandidát podle historického PID a času; není důkaz stejné životnosti.",
                ProcessTrafficAttribution.Direct => "Identita pochází přímo z události; zachycený provoz přesto nemusí být úplný.",
                _ => "Životnost se nepodařilo určit; tento přenos není přiřazen současnému procesu."
            };
            return KindText + " · " + NameText + "\n" + TargetText + "\n" + IdentityText + "\n" + attribution
                + "\nVzorek " + item.SampleId + " · epocha přiřazení " + item.AttributionEpoch
                + "\nPozorované bajty (přesný čítač): " + item.ObservedBytes.ToString(CultureInfo.InvariantCulture)
                + "\nPočet pozorovaných událostí: " + item.Operations.ToString(CultureInfo.InvariantCulture)
                + "\nStav měření: " + item.State + " · stav identity: " + item.IdentityState + "\nPříznaky: " + item.Flags
                + "\nKandidát generace: " + item.IdentityKind + " · " + item.Generation.ToString(CultureInfo.InvariantCulture)
                + "\nBoot: " + boot.High.ToString("X16", CultureInfo.InvariantCulture) + boot.Low.ToString("X16", CultureInfo.InvariantCulture)
                + "\nAgregační okno QPC: [" + item.StartQpc + ", " + item.EndQpc + ") · frekvence " + frequency
                + "\nVýpočetní interval: " + item.Measurement.IntervalNs + " ns · jednotka " + item.Measurement.Unit
                + "\nPřijato (monotónní ns): " + item.ObservedAtNs
                + "\nJde pouze o události zachycené vlastní relací ETW, nikoli o celkový provoz adaptéru ani obecné procesové I/O."
                + "\nSíťový počet vychází z velikostí událostí TCP/UDP; není zárukou počtu aplikačních bajtů."
                + "\nPotvrzení nového základního seznamu nezpřesní dřívější nejasné přenosy. Žádné živé akce podle PID.";
        }
    }
}

internal sealed class ProcessTrafficRows
{
    internal const int MaximumRows = 4096, MaximumEstimatedBytes = 3 * 1024 * 1024;
    private readonly LinkedList<ProcessEventRow> rows = new();
    private readonly Dictionary<(ulong Epoch, ulong Id), LinkedListNode<ProcessEventRow>> index = new();
    internal int Count => rows.Count;
    internal int EstimatedBytes { get; private set; }
    internal ulong Trimmed { get; private set; }
    internal IEnumerable<ProcessEventRow> Values => rows;

    internal void Upsert(ProcessTrafficSample item, long frequency, long origin, BootIdentifier boot)
    {
        var key = (item.AttributionEpoch, item.SampleId);
        var row = new ProcessEventRow(item, frequency, origin, boot);
        if (index.TryGetValue(key, out var existing)) {
            EstimatedBytes -= existing.Value.EstimatedBytes; existing.Value = row;
        } else index.Add(key, rows.AddLast(row));
        EstimatedBytes += row.EstimatedBytes;
        while (rows.Count > MaximumRows || EstimatedBytes > MaximumEstimatedBytes) {
            var first = rows.First!; var source = first.Value.Traffic!.Value;
            EstimatedBytes -= first.Value.EstimatedBytes; index.Remove((source.AttributionEpoch, source.SampleId)); rows.RemoveFirst(); ++Trimmed;
        }
    }
    internal void Clear() { rows.Clear(); index.Clear(); EstimatedBytes = 0; Trimmed = 0; }
}
