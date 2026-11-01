using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed class ProcessLifetimeStore
{
    internal const int MaximumRows = 4096, MaximumEstimatedBytes = 2 * 1024 * 1024;
    private readonly LinkedList<ProcessEventRow> rows = new();
    private readonly Dictionary<(ulong Segment, ProcessKey Key), LinkedListNode<ProcessEventRow>> index = new();
    private ProcessEventBatchInfo? previous;
    private ulong capture, segment = 1, nextId, lastSequence;
    private long latestQpc, latestFrequency, segmentOrigin;
    private bool hasSegmentOrigin, resetSegmentOrigin;
    private ProcessEventMonitorState state;
    private ProcessLifetimeIssues segmentIssues;
    internal int Count => rows.Count;
    internal int EstimatedBytes { get; private set; }
    internal ulong Trimmed { get; private set; }
    internal ulong MissingSequences { get; private set; }
    internal ulong GapBoundaries { get; private set; }
    internal ulong Unverified { get; private set; }
    internal ulong Conflicts { get; private set; }
    internal ulong Late { get; private set; }
    internal IEnumerable<ProcessEventRow> Values => rows;

    internal void BeginCapture(ulong epoch)
    {
        Clear(); capture = epoch; segment = 1; previous = null; lastSequence = 0; latestQpc = latestFrequency = 0; hasSegmentOrigin = resetSegmentOrigin = false;
        segmentIssues = ProcessLifetimeIssues.None;
    }
    internal void Clear()
    {
        rows.Clear(); index.Clear(); EstimatedBytes = 0; Trimmed = MissingSequences = GapBoundaries = Unverified = Conflicts = Late = 0;
    }
    internal bool ObserveBatch(ProcessEventBatchInfo info, IReadOnlyList<ProcessEventInfo> events, long originQpc)
    {
        bool changed = false;
        bool gap = previous is { } old ? LossChanged(old, info) || lastSequence != 0 && old.ResyncVersion != info.ResyncVersion : HasLoss(info);
        var boundary = lastSequence != 0 && previous is { } before ? BoundaryReason(before, info) : ProcessLifetimeIssues.None;
        if (boundary != ProcessLifetimeIssues.None) { Split(boundary); changed = true; }
        if (gap) { MarkGap(); changed = true; }
        if (state != info.State) {
            state = info.State;
            for (var node = rows.First; node is not null; node = node.Next) Replace(node, node.Value.Lifetime! with { CaptureState = state });
            changed |= rows.Count > 0;
        }
        foreach (var item in events) {
            bool reset = lastSequence != 0 && item.Sequence <= lastSequence;
            if (reset) { Split(ProcessLifetimeIssues.SequenceReset); gap = true; }
            else if (lastSequence != 0 && item.Sequence - lastSequence > 1) {
                MissingSequences = Add(MissingSequences, item.Sequence - lastSequence - 1); MarkGap(); gap = true;
            }
            lastSequence = item.Sequence;
            if (!hasSegmentOrigin) { segmentOrigin = resetSegmentOrigin ? item.TimestampQpc : originQpc; hasSegmentOrigin = true; resetSegmentOrigin = false; }
            var observation = new ProcessLifetimeObservation(item, info.QpcFrequency, segmentOrigin, info.Boot, info.BootState);
            var issues = segmentIssues | (gap ? ProcessLifetimeIssues.Gap : ProcessLifetimeIssues.None);
            if (item.Flags.HasFlag(ProcessEventFlags.Partial)) issues |= ProcessLifetimeIssues.Partial;
            if (Trimmed != 0) issues |= ProcessLifetimeIssues.Trimmed;
            if (info.QpcFrequency > 0 && latestFrequency == info.QpcFrequency && item.TimestampQpc < latestQpc) {
                issues |= ProcessLifetimeIssues.Late; Late = Add(Late, 1);
            }
            if (latestFrequency != info.QpcFrequency) latestQpc = 0;
            latestFrequency = info.QpcFrequency; latestQpc = Math.Max(latestQpc, item.TimestampQpc);
            AddObservation(observation, issues); changed = true;
        }
        Trim(); previous = info;
        return changed;
    }
    private void AddObservation(ProcessLifetimeObservation observation, ProcessLifetimeIssues issues)
    {
        var item = observation.Event;
        ProcessKey? key = observation.BootState == SampleState.Valid && !observation.Boot.IsEmpty && item.Pid != 0
            && item.IdentityState == SampleState.Valid && item.IdentityKind == GenerationKind.CreationTime && item.Generation != 0
            ? new ProcessKey(observation.Boot, item.Pid, item.IdentityKind, item.Generation) : null;
        if (key is null) { issues |= ProcessLifetimeIssues.Unverified; Unverified = Add(Unverified, 1); }
        if (key is { } verified && index.TryGetValue((segment, verified), out var existing)) {
            var entry = existing.Value.Lifetime!;
            var endpoint = item.Kind == ProcessEventKind.Started ? entry.Start : entry.Stop;
            if (entry.Issues.HasFlag(ProcessLifetimeIssues.Conflict)) {
                issues |= ProcessLifetimeIssues.Conflict; Conflicts = Add(Conflicts, 1);
            } else if (endpoint is { } observed) {
                if (SameEndpoint(observed, observation)) {
                    Replace(existing, entry with { Duplicates = Add(entry.Duplicates, 1), Issues = entry.Issues | issues | ProcessLifetimeIssues.Duplicate });
                    Touch(existing); Trim(); return;
                }
                Replace(existing, entry with { Issues = entry.Issues | ProcessLifetimeIssues.Conflict });
                issues |= ProcessLifetimeIssues.Conflict; Conflicts = Add(Conflicts, 1); Touch(existing);
            } else {
                bool lateStart = item.Kind == ProcessEventKind.Started && entry.Stop is not null;
                if (lateStart && !issues.HasFlag(ProcessLifetimeIssues.Late)) Late = Add(Late, 1);
                var paired = entry with {
                    Start = item.Kind == ProcessEventKind.Started ? observation : entry.Start,
                    Stop = item.Kind == ProcessEventKind.Stopped ? observation : entry.Stop,
                    Issues = entry.Issues | issues | (lateStart ? ProcessLifetimeIssues.Late : ProcessLifetimeIssues.None)
                };
                if (paired.Start is { } start && paired.Stop is { } stop && start.Frequency > 0 && start.Frequency == stop.Frequency
                    && stop.Event.TimestampQpc < start.Event.TimestampQpc) {
                    paired = paired with { Issues = paired.Issues | ProcessLifetimeIssues.Conflict }; Conflicts = Add(Conflicts, 1);
                }
                Replace(existing, paired); Touch(existing); Trim(); return;
            }
        }
        var value = new ProcessLifetimeEntry(++nextId, capture, segment, key,
            item.Kind == ProcessEventKind.Started ? observation : null, item.Kind == ProcessEventKind.Stopped ? observation : null,
            issues, 0, state);
        var row = new ProcessEventRow(value); var node = rows.AddLast(row); EstimatedBytes += row.EstimatedBytes;
        if (key is { } identity && !issues.HasFlag(ProcessLifetimeIssues.Conflict)) index[(segment, identity)] = node;
        Trim();
    }
    private void Replace(LinkedListNode<ProcessEventRow> node, ProcessLifetimeEntry value)
    {
        EstimatedBytes -= node.Value.EstimatedBytes; node.Value = new ProcessEventRow(value); EstimatedBytes += node.Value.EstimatedBytes;
    }
    private void Touch(LinkedListNode<ProcessEventRow> node) { rows.Remove(node); rows.AddLast(node); }
    private void Trim()
    {
        while (rows.Count > MaximumRows || EstimatedBytes > MaximumEstimatedBytes) {
            var first = rows.First!; var entry = first.Value.Lifetime!;
            if (entry.Key is { } key && index.TryGetValue((entry.Segment, key), out var indexed) && ReferenceEquals(first, indexed)) index.Remove((entry.Segment, key));
            EstimatedBytes -= first.Value.EstimatedBytes; rows.RemoveFirst(); Trimmed = Add(Trimmed, 1);
        }
    }
    private void Split(ProcessLifetimeIssues reason)
    {
        ++segment; index.Clear(); latestQpc = latestFrequency = 0; hasSegmentOrigin = false; resetSegmentOrigin = true; GapBoundaries = Add(GapBoundaries, 1);
        segmentIssues = reason | ProcessLifetimeIssues.Boundary;
        for (var node = rows.First; node is not null; node = node.Next) {
            var entry = node.Value.Lifetime!;
            if (entry.Start is null || entry.Stop is null) Replace(node, entry with { Issues = entry.Issues | segmentIssues | ProcessLifetimeIssues.Gap });
        }
    }
    private void MarkGap()
    {
        GapBoundaries = Add(GapBoundaries, 1);
        for (var node = rows.First; node is not null; node = node.Next) {
            var entry = node.Value.Lifetime!;
            if (entry.Start is null || entry.Stop is null) Replace(node, entry with { Issues = entry.Issues | ProcessLifetimeIssues.Gap });
        }
    }
    private static bool SameEndpoint(ProcessLifetimeObservation a, ProcessLifetimeObservation b) => a.Frequency == b.Frequency
        && a.Event.Kind == b.Event.Kind && a.Event.TimestampQpc == b.Event.TimestampQpc && a.Event.Flags == b.Event.Flags
        && a.Event.ParentPid == b.Event.ParentPid && a.Event.ExitStatus == b.Event.ExitStatus && a.Event.ImageName == b.Event.ImageName;
    private static bool HasLoss(ProcessEventBatchInfo info) => info.ProviderEventsLost != 0 || info.ProviderBuffersLost != 0 || info.QueueDropped != 0 || info.DecodeErrors != 0;
    private static bool LossChanged(ProcessEventBatchInfo a, ProcessEventBatchInfo b) => a.ProviderEventsLost != b.ProviderEventsLost
        || a.ProviderBuffersLost != b.ProviderBuffersLost || a.QueueDropped != b.QueueDropped || a.DecodeErrors != b.DecodeErrors;
    private static ProcessLifetimeIssues BoundaryReason(ProcessEventBatchInfo a, ProcessEventBatchInfo b)
    {
        var reason = ProcessLifetimeIssues.None;
        if (a.Boot != b.Boot) reason |= ProcessLifetimeIssues.BootChanged;
        if (a.QpcFrequency != b.QpcFrequency) reason |= ProcessLifetimeIssues.ClockChanged;
        if (b.ProviderEventsLost < a.ProviderEventsLost || b.ProviderBuffersLost < a.ProviderBuffersLost
            || b.QueueDropped < a.QueueDropped || b.DecodeErrors < a.DecodeErrors) reason |= ProcessLifetimeIssues.CounterReset;
        return reason;
    }
    private static ulong Add(ulong a, ulong b) => ulong.MaxValue - a < b ? ulong.MaxValue : a + b;
}
