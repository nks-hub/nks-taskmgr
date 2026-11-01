using System.Diagnostics;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed record CapabilityObservation(ProcessKey Target, string Status, DateTimeOffset ReceivedAt)
{
    internal long StoredAt { get; } = Stopwatch.GetTimestamp();
}

internal static class CapabilityReports
{
    private static readonly object sync = new();
    private static readonly Dictionary<string, CapabilityObservation> observations = new(StringComparer.Ordinal);
    private static readonly HashSet<string> commands = new(StringComparer.Ordinal) { "Threads", "Modules", "Signature", "Memory", "Handles", "HandleInfo", "Owners", "Events", "Actions", "Dumps", "ServiceActions" };
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

    internal static void ObserveEvents(ProcessEventBatchInfo info)
    {
        string status = info.State switch {
            ProcessEventMonitorState.Idle => "Zatím nespuštěno", ProcessEventMonitorState.Starting => "Spouští se",
            ProcessEventMonitorState.Running => "Záznam běží", ProcessEventMonitorState.Stopping => "Zastavuje se",
            ProcessEventMonitorState.Stopped => "Záznam zastavený", ProcessEventMonitorState.AccessDenied => "Přístup zamítnut",
            ProcessEventMonitorState.Unsupported => "Nepodporováno", ProcessEventMonitorState.Busy => "Předchozí relace není uklizená",
            ProcessEventMonitorState.StopTimedOut => "Zastavení překročilo limit", ProcessEventMonitorState.CleanupFailed => "Úklid nebyl potvrzený", _ => "Chyba záznamu"
        };
        if (info.Win32Error != 0) status += " · Windows " + info.Win32Error;
        Store("Events", new(default, status, DateTimeOffset.UtcNow));
    }

    internal static void ObserveActions(ProcessActionsBatch batch)
    {
        string status = batch.Frame == ProcessActionFrame.Results
            ? string.Join(" · ", batch.Results.GroupBy(row => row.Outcome).Select(group => group.Count() + "× " + ActionState(group.Key)))
            : "Požadavek nedokončený · Windows " + batch.Error;
        var target = batch.Results.Length == 1 ? batch.Results[0].Target.Key : default;
        Store("Actions", new(target, status, batch.ReceivedAt));
    }

    internal static void ObserveDump(ProcessKey target, string status) => Store("Dumps", new(target, Bound(status), DateTimeOffset.UtcNow));
    internal static void ObserveServiceAction(string status) => Store("ServiceActions", new(default, Bound(status), DateTimeOffset.UtcNow));
    private static string Bound(string text) => text.Length <= 240 ? text : text[..(char.IsHighSurrogate(text[239]) ? 239 : 240)] + "…";

    private static string ActionState(ProcessActionOutcome value) => value switch {
        ProcessActionOutcome.Inspected => "ověřený dotaz", ProcessActionOutcome.Applied => "potvrzená změna",
        ProcessActionOutcome.CloseRequested => "žádost o zavření", ProcessActionOutcome.TerminationRequested => "žádost o ukončení",
        ProcessActionOutcome.NativeRequestAccepted => "přijatý nativní požadavek", ProcessActionOutcome.Refused => "odmítnuto",
        ProcessActionOutcome.IdentityChanged => "změna identity", ProcessActionOutcome.AlreadyExited => "již skončeno",
        ProcessActionOutcome.Unsupported => "nepodporováno", ProcessActionOutcome.Unverified => "neověřeno", _ => "chyba"
    };

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
