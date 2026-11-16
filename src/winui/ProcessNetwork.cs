using System.Diagnostics;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

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
