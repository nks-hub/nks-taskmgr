using System.Diagnostics;
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
        KindText = source + (item.Direction switch { ProcessTrafficDirection.Read => " " + Loc.T("čt."), ProcessTrafficDirection.Write => " " + Loc.T("záp."), ProcessTrafficDirection.Sent => " ↑", _ => " ↓" });
        double elapsed = frequency > 0 ? (item.StartQpc - origin) / (double)frequency : 0;
        TimeText = frequency > 0 ? (elapsed >= 0 ? "+" : "") + elapsed.ToString("N3", Czech) + " s" : Loc.T("Čas ETW nedostupný");
        NameText = Loc.F("{0} B pozorováno · {1} událostí · {2} B/s v okně 1 s", item.ObservedBytes.ToString("N0", Czech),
            item.Operations.ToString("N0", Czech), item.ObservedBytesPerSecond.ToString("N1", Czech));
        TargetText = (item.Pid == 0 ? Loc.T("PID nelze určit") : "PID " + item.Pid.ToString(CultureInfo.InvariantCulture))
            + " · " + source + (item.Source != ProcessTrafficSource.PhysicalDisk ? (item.Flags.HasFlag(ProcessTrafficFlags.Ipv6) ? " IPv6" : " IPv4") : " · " + Loc.T("fyzické I/O"));
        IdentityText = item.Attribution switch {
            ProcessTrafficAttribution.Correlated => Loc.T("Částečné · korelace PID/času · životnost nepotvrzená"),
            ProcessTrafficAttribution.Direct => Loc.T("Pozorovaná podmnožina · PID z události, generace z nepřerušené evidence"),
            ProcessTrafficAttribution.IssuingThreadOwner => Loc.T("Pozorovaná podmnožina · vlastník vydávajícího vlákna při zahájení"),
            _ => Loc.T("Částečné · nelze přiřadit historické životnosti")
        };
        if (item.Flags.HasFlag(ProcessTrafficFlags.IdentityConflict)) IdentityText += " · " + Loc.T("dřívější přiřazení odvoláno");
        if (item.Flags.HasFlag(ProcessTrafficFlags.CounterOverflow)) IdentityText += " · " + Loc.T("čítač přetekl");
        if (item.Flags.HasFlag(ProcessTrafficFlags.WindowOpen)) IdentityText += " · " + Loc.T("okno není uzavřené");
        EstimatedBytes = 1024 + 2 * (NameText.Length + TargetText.Length + TimeText.Length + KindText.Length + IdentityText.Length);
    }
    private static CultureInfo Czech => Loc.Culture;
    private string TrafficDetail {
        get {
            var item = Traffic!.Value;
            string attribution = item.Attribution switch {
                ProcessTrafficAttribution.Correlated => Loc.T("Kandidát podle historického PID a času; není důkaz stejné životnosti."),
                ProcessTrafficAttribution.Direct => Loc.T("PID pochází z události TCP/UDP; generaci určila evidence vzniku a ukončení bez ztráty od výchozího seznamu procesů nebo od vzniku procesu. Zachycený provoz přesto nemusí být úplný."),
                ProcessTrafficAttribution.IssuingThreadOwner => Loc.T("INIT a dokončení téhož I/O doložily životnost vlastníka vydávajícího vlákna při zahájení. Nejde o důkaz obecného žadatele I/O; vlákno může být připojené k jinému procesu."),
                _ => Loc.T("Životnost se nepodařilo určit; tento přenos není přiřazen současnému procesu.")
            };
            string generation = item.Generation.ToString(CultureInfo.InvariantCulture);
            return KindText + " · " + NameText + "\n" + TargetText + "\n" + IdentityText + "\n" + attribution
                + "\n" + Loc.F("Vzorek {0} · epocha přiřazení {1}", item.SampleId, item.AttributionEpoch)
                + "\n" + Loc.F("Pozorované bajty (přesný čítač): {0}", item.ObservedBytes.ToString(CultureInfo.InvariantCulture))
                + "\n" + Loc.F("Počet pozorovaných událostí: {0}", item.Operations.ToString(CultureInfo.InvariantCulture))
                + "\n" + Loc.F("Stav měření: {0} · stav identity: {1}", item.State, item.IdentityState) + "\n" + Loc.F("Příznaky: {0}", item.Flags)
                + "\n" + (item.Attribution switch {
                    ProcessTrafficAttribution.IssuingThreadOwner => Loc.F("Životnost vlastníka vlákna: {0} · {1}", item.IdentityKind, generation),
                    ProcessTrafficAttribution.Direct => Loc.F("Generace: {0} · {1}", item.IdentityKind, generation),
                    _ => Loc.F("Kandidát generace: {0} · {1}", item.IdentityKind, generation) })
                + "\nBoot: " + boot.High.ToString("X16", CultureInfo.InvariantCulture) + boot.Low.ToString("X16", CultureInfo.InvariantCulture)
                + "\n" + Loc.F("Agregační okno QPC: [{0}, {1}) · frekvence {2}", item.StartQpc, item.EndQpc, frequency)
                + "\n" + Loc.F("Výpočetní interval: {0} ns · jednotka {1}", item.Measurement.IntervalNs, item.Measurement.Unit)
                + "\n" + Loc.F("Přijato (monotónní ns): {0}", item.ObservedAtNs)
                + "\n" + Loc.T("Jde pouze o události zachycené vlastní relací ETW, nikoli o celkový provoz adaptéru ani obecné procesové I/O.")
                + "\n" + Loc.T("Počet síťových bajtů vychází z velikostí událostí TCP/UDP; nemusí odpovídat počtu bajtů aplikace.")
                + "\n" + Loc.T("Potvrzení nového základního seznamu nezpřesní dřívější nejasné přenosy. Žádné živé akce podle PID.");
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
