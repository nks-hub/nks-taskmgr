using System.Diagnostics;

namespace NksTaskMgr.WinUI;

internal sealed record CapabilityObservation(ProcessKey Target, string Status, DateTimeOffset ReceivedAt)
{
    internal long StoredAt { get; } = Stopwatch.GetTimestamp();
}

internal static class CapabilityReports
{
    private static readonly object sync = new();
    private static readonly Dictionary<string, CapabilityObservation> observations = new(StringComparer.Ordinal);
    private static readonly HashSet<string> commands = new(StringComparer.Ordinal) { "Threads", "Modules", "Signature", "Memory", "Handles", "HandleInfo", "Owners" };
    private static long version;
    internal static long Version => Interlocked.Read(ref version);

    internal static void Observe(ProcessKey key, ProcessDetailsSnapshot result)
    {
        string status = result.State switch {
            ProcessDetailsState.Ok => result.PartialMetrics ? "Část údajů chybí" : "Dotaz dokončen",
            ProcessDetailsState.AccessDenied => "Přístup zamítnut",
            ProcessDetailsState.Unsupported => "Nepodporováno",
            ProcessDetailsState.TargetChanged => "Cíl už neodpovídá původnímu procesu",
            ProcessDetailsState.LimitExceeded => "Dosažen limit čtení",
            ProcessDetailsState.InvalidRequest => "Požadavek nebylo možné přijmout",
            ProcessDetailsState.Unavailable => "Údaj není dostupný", _ => "Dotaz skončil chybou"
        };
        Store(result.Command.ToString(), new(key, status, result.ReceivedAt));
    }

    internal static void Failed(ProcessDetailsCommand command, ProcessKey key, string status)
    {
        if (status.Length > 200) { int length = char.IsHighSurrogate(status[199]) ? 199 : 200; status = status[..length] + "…"; }
        Store(command.ToString(), new(key, status, DateTimeOffset.UtcNow));
    }

    private static void Store(string command, CapabilityObservation value)
    {
        if (!commands.Contains(command)) return;
        lock (sync) {
            if (observations.TryGetValue(command, out var prior) && prior.Target == value.Target && prior.Status == value.Status
                && Stopwatch.GetElapsedTime(prior.StoredAt, value.StoredAt) < TimeSpan.FromSeconds(1)) return;
            observations[command] = value; Interlocked.Increment(ref version);
        }
    }

    internal static IReadOnlyDictionary<string, CapabilityObservation> Snapshot()
    {
        lock (sync) return new Dictionary<string, CapabilityObservation>(observations, StringComparer.Ordinal);
    }
}
