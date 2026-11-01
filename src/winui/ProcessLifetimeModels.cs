using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

[Flags]
internal enum ProcessLifetimeIssues {
    None = 0, Unverified = 1, Gap = 2, Late = 4, Conflict = 8, Duplicate = 16, Boundary = 32, Trimmed = 64,
    ClockChanged = 128, SequenceReset = 256, BootChanged = 512, CounterReset = 1024, Partial = 2048
}
internal readonly record struct ProcessLifetimeObservation(ProcessEventInfo Event, long Frequency, long Origin, BootIdentifier Boot, SampleState BootState);
internal sealed record ProcessLifetimeEntry(ulong Id, ulong Capture, ulong Segment, ProcessKey? Key,
    ProcessLifetimeObservation? Start, ProcessLifetimeObservation? Stop, ProcessLifetimeIssues Issues, ulong Duplicates, ProcessEventMonitorState CaptureState)
{
    internal bool Paired => Key is not null && Start is not null && Stop is not null && !Issues.HasFlag(ProcessLifetimeIssues.Conflict);
    internal double? DurationSeconds => Paired && Start is { } first && Stop is { } last && first.Frequency > 0
        && first.Frequency == last.Frequency && first.Boot == last.Boot && last.Event.TimestampQpc >= first.Event.TimestampQpc
        ? (last.Event.TimestampQpc - first.Event.TimestampQpc) / (double)first.Frequency : null;
}
