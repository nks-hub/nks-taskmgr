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
                + "\n" + Loc.T("Síťový počet vychází z velikostí událostí TCP/UDP; není zárukou počtu aplikačních bajtů.")
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

/// <summary>Observed TCP/UDP bytes of one process generation: the last complete 1 s window and the sum since capture start.</summary>
internal readonly record struct ProcessNetworkRate(double SentPerSecond, double ReceivedPerSecond, ulong TotalBytes)
{
    internal double PerSecond => SentPerSecond + ReceivedPerSecond;
}

/// <summary>Network rates per process generation from the event window's own ETW capture (needs an elevated token).</summary>
/// <remarks>Only Direct samples count. Unknown bytes in the shown window make every value a lower bound.
/// Samples upsert by epoch and sample id, so a revoked sample leaves the sums.</remarks>
internal static class ProcessNetwork
{
    internal const int MaximumSamples = 8192;
    // Native drains 2 s after delivery and ETW flushes every 1 s; older windows no longer receive bytes.
    private const long CompleteAfterWindows = 5;
    private static readonly Dictionary<(ulong Epoch, ulong Id), ProcessTrafficSample> samples = [];
    private static readonly Queue<(ulong Epoch, ulong Id)> order = new();
    private static BootIdentifier boot;
    private static long frequency, coveredFrom;
    private static bool running, trimmed;

    internal static void Observe(ProcessEventBatchInfo info, ProcessTrafficBatch? batch)
    {
        if (batch is null || info.State != ProcessEventMonitorState.Running || info.BootState != SampleState.Valid
            || info.QpcFrequency != Stopwatch.Frequency || batch.Info.NetworkState is not (SampleState.First or SampleState.Gap or SampleState.Valid)
            || batch.Info.IdentityProviderState is not (SampleState.First or SampleState.Valid)) { Clear(); return; }
        if (!running || info.Boot != boot) {
            Clear(); running = true; boot = info.Boot; frequency = info.QpcFrequency;
            coveredFrom = Stopwatch.GetTimestamp() + frequency;
        }
        foreach (var sample in batch.Samples) {
            if (sample.Source == ProcessTrafficSource.PhysicalDisk) continue;
            var key = (sample.AttributionEpoch, sample.SampleId);
            if (!samples.ContainsKey(key)) order.Enqueue(key);
            samples[key] = sample;
        }
        while (samples.Count > MaximumSamples) { samples.Remove(order.Dequeue()); trimmed = true; }
    }

    internal static void Clear() { samples.Clear(); order.Clear(); running = trimmed = false; }

    /// <summary>Rates of the newest complete window; null while there is no complete window of a running capture.</summary>
    internal static Dictionary<ProcessKey, ProcessNetworkRate>? Rates(out bool partial, out bool totalPartial, long? nowQpc = null)
    {
        partial = totalPartial = trimmed;
        if (!running) return null;
        long window = ((nowQpc ?? Stopwatch.GetTimestamp()) / frequency - CompleteAfterWindows) * frequency;
        if (window < coveredFrom) return null;
        var rates = new Dictionary<ProcessKey, ProcessNetworkRate>();
        foreach (var sample in samples.Values) {
            if (sample.Attribution != ProcessTrafficAttribution.Direct || sample.IdentityKind != GenerationKind.CreationTime) {
                totalPartial = true; partial |= sample.StartQpc == window; continue;
            }
            var key = new ProcessKey(boot, sample.Pid, GenerationKind.CreationTime, sample.Generation);
            rates.TryGetValue(key, out var rate);
            double bytes = sample.StartQpc == window ? sample.ObservedBytes : 0;
            rates[key] = new(rate.SentPerSecond + (sample.Direction == ProcessTrafficDirection.Sent ? bytes : 0),
                rate.ReceivedPerSecond + (sample.Direction == ProcessTrafficDirection.Received ? bytes : 0),
                ulong.MaxValue - rate.TotalBytes < sample.ObservedBytes ? ulong.MaxValue : rate.TotalBytes + sample.ObservedBytes);
        }
        return rates;
    }
}
