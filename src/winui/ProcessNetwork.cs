using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

/// <summary>
/// Network bytes of one process generation. With the ETW capture: the last complete 1 s window split by direction and
/// the sum since capture start. Without it (<paramref name="Split"/> false): both directions in SentPerSecond and the
/// lifetime sum of the process, as the kernel's energy accounting does not separate them.
/// </summary>
internal readonly record struct ProcessNetworkRate(double SentPerSecond, double ReceivedPerSecond, ulong TotalBytes, bool Split = true)
{
    internal double PerSecond => SentPerSecond + ReceivedPerSecond;
    /// <summary>Bytes per second in megabits like the native Task Manager.</summary>
    internal static string Text(double bytesPerSecond) => Display.Number(bytesPerSecond / 125000, " Mb/s");
    internal string Detail(string bound) => Split
        ? Loc.F("Síť: příjem {0} · odeslání {1}", bound + Text(ReceivedPerSecond), bound + Text(SentPerSecond))
        : Loc.F("Síť: {0} (příjem a odeslání dohromady)", bound + Text(PerSecond));
    internal string TotalText => (Split ? Loc.T("Síť od spuštění záznamu:") : Loc.T("Síť od spuštění procesu:")) + " ";
}

/// <summary>
/// Network bytes per process from the kernel's energy accounting (NtQueryInformationProcess ProcessEnergyValues,
/// NetworkTxRxBytes), readable with PROCESS_QUERY_LIMITED_INFORMATION by a standard user. The native Task Manager shows
/// the same counter through SRUM; received and sent bytes are one sum. Handles stay open between snapshots.
/// </summary>
internal static partial class ProcessEnergyNetwork
{
    private const int ProcessEnergyValues = 76, NetworkTxRxBytes = 88, ProcessSequenceNumber = 92;
    private static readonly Dictionary<ProcessKey, Counter> counters = [];
    private static readonly byte[] buffer = new byte[512];

    private sealed class Counter(nint handle) { internal readonly nint Handle = handle; internal ulong Bytes; internal long Timestamp; }

    /// <summary>Rates since the previous call for the given generations; a generation seen for the first time or unreadable has none.</summary>
    internal static Dictionary<ProcessKey, ProcessNetworkRate> Rates(IEnumerable<ProcessKey> keys)
    {
        var rates = new Dictionary<ProcessKey, ProcessNetworkRate>();
        var seen = new HashSet<ProcessKey>();
        foreach (var key in keys) {
            if (key.Kind is not (GenerationKind.CreationTime or GenerationKind.SequenceNumber) || key.Pid == 0 || !seen.Add(key)) continue;
            bool known = counters.TryGetValue(key, out var counter);
            if (!known && (counter = Open(key)) is null) continue;
            long now = Stopwatch.GetTimestamp();
            if (NtQueryInformationProcess(counter!.Handle, ProcessEnergyValues, buffer, buffer.Length, out int returned) < 0 || returned < NetworkTxRxBytes + 8) continue;
            ulong bytes = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(NetworkTxRxBytes));
            if (known && bytes >= counter.Bytes && now > counter.Timestamp)
                rates[key] = new((bytes - counter.Bytes) * (double)Stopwatch.Frequency / (now - counter.Timestamp), 0, bytes, Split: false);
            counter.Bytes = bytes; counter.Timestamp = now;
            counters[key] = counter;
        }
        foreach (var key in counters.Keys.Where(key => !seen.Contains(key)).ToArray()) { CloseHandle(counters[key].Handle); counters.Remove(key); }
        return rates;
    }

    /// <summary>Closes every handle, for example when the ETW capture takes over.</summary>
    internal static void Clear()
    {
        foreach (var counter in counters.Values) CloseHandle(counter.Handle);
        counters.Clear();
    }

    private static Counter? Open(ProcessKey key)
    {
        nint process = OpenProcess(0x1000, false, key.Pid);
        if (process == 0) return null;
        // A reused PID must not inherit the counter of an earlier process.
        if ((key.Kind == GenerationKind.SequenceNumber ? SequenceNumber(process) : GetProcessTimes(process, out long created, out _, out _, out _) ? (ulong)created : 0) == key.Generation)
            return new Counter(process);
        CloseHandle(process);
        return null;
    }

    /// <summary>The process sequence number that newer Windows builds use as the process generation; 0 when unavailable.</summary>
    internal static ulong SequenceNumber(nint process)
    {
        var value = new byte[8];
        return NtQueryInformationProcess(process, ProcessSequenceNumber, value, value.Length, out int returned) >= 0 && returned == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(value) : 0;
    }

    [LibraryImport("kernel32.dll")] private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint handle);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [LibraryImport("ntdll.dll")] private static partial int NtQueryInformationProcess(nint process, int informationClass, byte[] information, int length, out int returned);
}

/// <summary>Network rates per process generation from the application-wide ETW capture in ProcessCapture (needs an elevated token).</summary>
/// <remarks>Only Direct samples count. Unknown bytes in the shown window make every value a lower bound.
/// Samples upsert by epoch and sample id, so a revoked sample leaves the sums.</remarks>
internal static class ProcessNetwork
{
    /// <summary>Column and caption help shared by every place that shows per-process network traffic.</summary>
    internal static string Help => Loc.T("Síťový provoz podle procesu. Bez práv správce ukazuje příjem a odeslání dohromady, stejně jako Správce úloh Windows; u systémových procesů, které Windows běžnému uživateli neotevře, je „—“. Se správcem měří zvlášť příjem a odeslání TCP/UDP, hodnota je pak za uzavřenou sekundu asi před 5 s a ≥ znamená, že část provozu nešlo přiřadit.");
    /// <summary>What the process I/O counters mean, so they are not read as disk activity.</summary>
    internal static string IoHelp => Loc.T("Čtení a zápis souborů, rour a zařízení dohromady, včetně souborů z mezipaměti; není to jen disk. Síť ukazuje zvlášť sloupec Síť.");
    internal const int MaximumSamples = 8192;
    // Native drains 2 s after delivery and ETW flushes every 1 s; older windows no longer receive bytes.
    private const long CompleteAfterWindows = 5;
    private static readonly Dictionary<(ulong Epoch, ulong Id), ProcessTrafficSample> samples = [];
    private static readonly Queue<(ulong Epoch, ulong Id)> order = new();
    // Disk samples of the same capture, kept apart so a busy disk never evicts network samples.
    private static readonly Dictionary<(ulong Epoch, ulong Id), ProcessTrafficSample> diskSamples = [];
    private static readonly Queue<(ulong Epoch, ulong Id)> diskOrder = new();
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
            bool disk = sample.Source == ProcessTrafficSource.PhysicalDisk;
            var key = (sample.AttributionEpoch, sample.SampleId);
            var store = disk ? diskSamples : samples;
            if (!store.ContainsKey(key)) (disk ? diskOrder : order).Enqueue(key);
            store[key] = sample;
        }
        while (samples.Count > MaximumSamples) { samples.Remove(order.Dequeue()); trimmed = true; }
        while (diskSamples.Count > MaximumSamples) diskSamples.Remove(diskOrder.Dequeue());
    }

    internal static void Clear() { samples.Clear(); order.Clear(); diskSamples.Clear(); diskOrder.Clear(); running = trimmed = false; }

    /// <summary>
    /// Bytes each process generation read and wrote on one physical disk in the newest complete window; null while the
    /// capture has no complete window. Only completions whose issuing thread names the process count.
    /// </summary>
    internal static Dictionary<ProcessKey, (double Read, double Write)>? DiskRates(uint disk, long? nowQpc = null)
    {
        if (!running) return null;
        long window = ((nowQpc ?? Stopwatch.GetTimestamp()) / frequency - CompleteAfterWindows) * frequency;
        if (window < coveredFrom) return null;
        var rates = new Dictionary<ProcessKey, (double Read, double Write)>();
        foreach (var sample in diskSamples.Values) {
            if (sample.Device != disk || sample.StartQpc != window || sample.Attribution != ProcessTrafficAttribution.IssuingThreadOwner
                || sample.IdentityKind != GenerationKind.CreationTime) continue;
            var key = new ProcessKey(boot, sample.Pid, GenerationKind.CreationTime, sample.Generation);
            rates.TryGetValue(key, out var rate);
            rates[key] = sample.Direction == ProcessTrafficDirection.Read ? (rate.Read + sample.ObservedBytes, rate.Write) : (rate.Read, rate.Write + sample.ObservedBytes);
        }
        return rates;
    }

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
