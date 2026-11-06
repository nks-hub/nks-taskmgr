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
            ProcessDetailsState.Ok => result.PartialMetrics ? Loc.T("Část údajů chybí") : Loc.T("Dotaz dokončen"),
            ProcessDetailsState.AccessDenied => Loc.T("Přístup zamítnut"),
            ProcessDetailsState.Unsupported => Loc.T("Nepodporováno"),
            ProcessDetailsState.TargetChanged => Loc.T("Cíl už neodpovídá původnímu procesu"),
            ProcessDetailsState.LimitExceeded => Loc.T("Dosažen limit čtení"),
            ProcessDetailsState.InvalidRequest => Loc.T("Požadavek nebylo možné přijmout"),
            ProcessDetailsState.Unavailable => Loc.T("Údaj není dostupný"), _ => Loc.T("Dotaz skončil chybou")
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
            ProcessEventMonitorState.Idle => Loc.T("Zatím nespuštěno"), ProcessEventMonitorState.Starting => Loc.T("Spouští se"),
            ProcessEventMonitorState.Running => Loc.T("Záznam běží"), ProcessEventMonitorState.Stopping => Loc.T("Zastavuje se"),
            ProcessEventMonitorState.Stopped => Loc.T("Záznam zastavený"), ProcessEventMonitorState.AccessDenied => Loc.T("Přístup zamítnut"),
            ProcessEventMonitorState.Unsupported => Loc.T("Nepodporováno"), ProcessEventMonitorState.Busy => Loc.T("Předchozí relace není uklizená"),
            ProcessEventMonitorState.StopTimedOut => Loc.T("Zastavení překročilo limit"), ProcessEventMonitorState.CleanupFailed => Loc.T("Úklid nebyl potvrzený"), _ => Loc.T("Chyba záznamu")
        };
        if (info.Win32Error != 0) status += " · Windows " + info.Win32Error;
        Store("Events", new(default, status, DateTimeOffset.UtcNow));
    }

    internal static void ObserveActions(ProcessActionsBatch batch)
    {
        string status = batch.Frame == ProcessActionFrame.Results
            ? string.Join(" · ", batch.Results.GroupBy(row => row.Outcome).Select(group => group.Count() + "× " + ActionState(group.Key)))
            : Loc.F("Požadavek nedokončený · Windows {0}", batch.Error);
        var target = batch.Results.Length == 1 ? batch.Results[0].Target.Key : default;
        Store("Actions", new(target, status, batch.ReceivedAt));
    }

    internal static void ObserveDump(ProcessKey target, string status) => Store("Dumps", new(target, Bound(status), DateTimeOffset.UtcNow));
    internal static void ObserveServiceAction(string status) => Store("ServiceActions", new(default, Bound(status), DateTimeOffset.UtcNow));
    private static string Bound(string text) => text.Length <= 240 ? text : text[..(char.IsHighSurrogate(text[239]) ? 239 : 240)] + "…";

    private static string ActionState(ProcessActionOutcome value) => value switch {
        ProcessActionOutcome.Inspected => Loc.T("ověřený dotaz"), ProcessActionOutcome.Applied => Loc.T("potvrzená změna"),
        ProcessActionOutcome.CloseRequested => Loc.T("žádost o zavření"), ProcessActionOutcome.TerminationRequested => Loc.T("žádost o ukončení"),
        ProcessActionOutcome.NativeRequestAccepted => Loc.T("přijatý nativní požadavek"), ProcessActionOutcome.Refused => Loc.T("odmítnuto"),
        ProcessActionOutcome.IdentityChanged => Loc.T("změna identity"), ProcessActionOutcome.AlreadyExited => Loc.T("již skončeno"),
        ProcessActionOutcome.Unsupported => Loc.T("nepodporováno"), ProcessActionOutcome.Unverified => Loc.T("neověřeno"), _ => Loc.T("chyba")
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
