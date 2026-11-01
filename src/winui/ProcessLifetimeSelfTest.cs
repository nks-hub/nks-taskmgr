using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static void ProcessLifetimes()
    {
        LifetimeVerifiedPairAndSelection();
        LifetimePidReuseAndBoot();
        LifetimeCaptureAndClear();
        LifetimeUnverifiedEvents();
        LifetimeLossAndMissingSequences();
        LifetimeSequenceAndCounterReset();
        LifetimeDelayedEvents();
        LifetimeDuplicatesAndConflicts();
        LifetimeQpcAvailability();
        LifetimeStoppedCapture();
        LifetimeBoundedTrim();
        App.Trace("Process lifetime regression checks passed: full identities, capture boundaries, loss, delayed/duplicate/conflicting events, QPC and bounded trim");
    }

    private static ProcessLifetimeStore LifetimeStore(ulong capture = 1)
    {
        var store = new ProcessLifetimeStore(); store.BeginCapture(capture); return store;
    }
    private static ProcessEventBatchInfo LifetimeInfo(long frequency = 1000) => new(ProcessEventMonitorState.Running, 0,
        0, new BootIdentifier(1, 2), SampleState.Valid, frequency, 0, 0, 0, 0, 0, 1);
    private static ProcessEventInfo LifetimeEvent(ulong sequence, ProcessEventKind kind, long qpc, uint pid = 100, ulong generation = 500,
        string name = "example.exe") => new(sequence, qpc, checked((long)sequence * 1_000_000), kind, pid, 0,
            SampleState.Valid, GenerationKind.CreationTime, generation, ProcessEventFlags.HasImageName, 0, name);
    private static ProcessLifetimeEntry LifetimeEntry(ProcessEventRow row) => row.Lifetime
        ?? throw new InvalidOperationException("Lifetime fixture returned a non-lifetime row.");

    private static void LifetimeVerifiedPairAndSelection()
    {
        var store = LifetimeStore(); var info = LifetimeInfo();
        var start = LifetimeEvent(1, ProcessEventKind.Started, 1000);
        store.ObserveBatch(info, [start], 1000);
        var held = store.Values.Single(); var selected = held.SelectionIdentity;
        var stop = LifetimeEvent(2, ProcessEventKind.Stopped, 3500) with { Flags = ProcessEventFlags.HasImageName | ProcessEventFlags.HasExitStatus };
        store.ObserveBatch(info, [stop], 1000);
        var row = store.Values.Single(); var lifetime = LifetimeEntry(row);
        Require(lifetime.Key == new ProcessKey(info.Boot, 100, GenerationKind.CreationTime, 500)
            && lifetime.Start?.Event == start && lifetime.Stop?.Event == stop && lifetime.DurationSeconds == 2.5,
            "Verified lifecycle endpoints did not retain their full identity, source values or measured QPC duration.");
        Require(row.SelectionIdentity == selected && !ReferenceEquals(held, row) && LifetimeEntry(held).Stop is null,
            "Closing a lifetime changed its selection key or mutated a previously held row.");
        Require(lifetime.Stop?.Event.Flags.HasFlag(ProcessEventFlags.HasExitStatus) == true
            && row.Detail.Contains("0x00000000", StringComparison.Ordinal), "A measured zero exit status was replaced with an unavailable value.");
        Require(row.LifetimeMatches(1) && !row.LifetimeMatches(2) && row.Matches("EXAMPLE.EXE"),
            "A completed lifecycle was not available to the production lifecycle filter and name search.");
    }

    private static void LifetimePidReuseAndBoot()
    {
        var store = LifetimeStore(); var info = LifetimeInfo();
        var oldStart = LifetimeEvent(1, ProcessEventKind.Started, 1000);
        var newStop = LifetimeEvent(2, ProcessEventKind.Stopped, 4000, generation: 501);
        store.ObserveBatch(info, [oldStart, newStop], 0);
        Require(store.Count == 2 && store.Values.All(row => !LifetimeEntry(row).Paired),
            "Reused PID and equal image names were incorrectly treated as one process lifetime.");
        store.ObserveBatch(info, [LifetimeEvent(3, ProcessEventKind.Stopped, 1500)], 0);
        var original = store.Values.Select(LifetimeEntry).Single(row => row.Key?.Generation == 500);
        Require(original.DurationSeconds == .5 && store.Values.Select(LifetimeEntry).Single(row => row.Key?.Generation == 501).Start is null,
            "A late end event was paired with the new owner of the same PID.");

        store = LifetimeStore(); store.ObserveBatch(info, [oldStart], 0);
        var otherBoot = info with { Boot = new BootIdentifier(info.Boot.Low, info.Boot.High ^ 1) };
        store.ObserveBatch(otherBoot, [LifetimeEvent(2, ProcessEventKind.Stopped, 2000)], 0);
        var boots = store.Values.Select(LifetimeEntry).ToArray();
        Require(boots.Length == 2 && boots.All(row => !row.Paired && row.DurationSeconds is null)
            && boots.Select(row => row.Key?.Boot).Distinct().Count() == 2
            && boots.Any(row => row.Issues.HasFlag(ProcessLifetimeIssues.BootChanged)),
            "The upper half of the boot identifier was ignored while pairing lifecycle events.");

        store = LifetimeStore();
        store.ObserveBatch(info, [oldStart, LifetimeEvent(2, ProcessEventKind.Stopped, 2000, pid: 101)], 0);
        Require(store.Count == 2 && store.Values.All(row => !LifetimeEntry(row).Paired),
            "Equal creation times in different PIDs were treated as one historical process.");
    }

    private static void LifetimeCaptureAndClear()
    {
        var store = LifetimeStore(10); var info = LifetimeInfo();
        store.ObserveBatch(info, [LifetimeEvent(1, ProcessEventKind.Started, 1000)], 0);
        var previous = store.Values.Single();
        store.BeginCapture(11);
        store.ObserveBatch(info, [LifetimeEvent(2, ProcessEventKind.Stopped, 2000)], 0);
        var current = store.Values.Single(); var entry = LifetimeEntry(current);
        Require(entry.Capture == 11 && entry.Start is null && entry.Stop is not null && entry.DurationSeconds is null
            && LifetimeEntry(previous).Capture == 10 && LifetimeEntry(previous).Stop is null
            && current.SelectionIdentity != previous.SelectionIdentity,
            "Restarting capture joined an old start event or reused an old capture selection identity.");
        store.Clear();
        Require(store.Count == 0 && store.EstimatedBytes == 0 && !store.Values.Any(), "Explicit clear left retained lifecycle data.");
        store.ObserveBatch(info, [LifetimeEvent(3, ProcessEventKind.Started, 2500)], 0);
        var afterClear = store.Values.Single();
        Require(LifetimeEntry(afterClear).Stop is null && afterClear.SelectionIdentity != current.SelectionIdentity,
            "Explicit clear retained an old endpoint or reused the removed selection key.");
    }

    private static void LifetimeUnverifiedEvents()
    {
        var store = LifetimeStore(); var info = LifetimeInfo();
        var start = LifetimeEvent(1, ProcessEventKind.Started, 1000) with {
            IdentityState = SampleState.Unavailable, IdentityKind = GenerationKind.Missing, Generation = 0
        };
        var stop = start with { Sequence = 2, Kind = ProcessEventKind.Stopped, TimestampQpc = 1001 };
        store.ObserveBatch(info, [start, stop], 0);
        Require(store.Count == 2 && store.Unverified == 2 && store.Values.All(row => LifetimeEntry(row).Key is null
            && LifetimeEntry(row).DurationSeconds is null && row.LifetimeMatches(4)),
            "Unverified events were paired from PID, image name or adjacent timestamps.");
        store = LifetimeStore();
        store.ObserveBatch(info, [LifetimeEvent(1, ProcessEventKind.Started, 1000), stop,
            LifetimeEvent(3, ProcessEventKind.Stopped, 2000)], 0);
        Require(store.Count == 2 && store.Values.Count(row => LifetimeEntry(row).Paired) == 1 && store.Unverified == 1,
            "An unverified stop consumed the verified start or prevented a later exact-identity match.");
        var missingBoot = info with { Boot = default, BootState = SampleState.Unavailable };
        store = LifetimeStore(); store.ObserveBatch(missingBoot, [start with { Pid = 0 }, stop with { Pid = 0 }], 0);
        Require(store.Values.All(row => LifetimeEntry(row).Key is null && !LifetimeEntry(row).Paired),
            "Missing boot and PID information became a historical identity.");
    }

    private static void LifetimeLossAndMissingSequences()
    {
        var store = LifetimeStore(); var info = LifetimeInfo();
        store.ObserveBatch(info, [LifetimeEvent(1, ProcessEventKind.Started, 1000)], 0);
        var lost = info with { ProviderEventsLost = 7, ProviderBuffersLost = 1, QueueDropped = 3, DecodeErrors = 2, ResyncVersion = 2 };
        store.ObserveBatch(lost, [LifetimeEvent(2, ProcessEventKind.Stopped, 5000)], 0);
        var entry = LifetimeEntry(store.Values.Single());
        Require(entry.Paired && entry.DurationSeconds == 4 && entry.Issues.HasFlag(ProcessLifetimeIssues.Gap)
            && store.GapBoundaries > 0 && store.MissingSequences == 0,
            "Reported source loss broke an exact-identity pair, changed its duration or invented a sequence-gap count.");

        store = LifetimeStore();
        store.ObserveBatch(info, [LifetimeEvent(10, ProcessEventKind.Started, 1000), LifetimeEvent(13, ProcessEventKind.Stopped, 2500)], 0);
        entry = LifetimeEntry(store.Values.Single());
        Require(entry.Paired && entry.DurationSeconds == 1.5 && entry.Issues.HasFlag(ProcessLifetimeIssues.Gap) && store.MissingSequences == 2,
            "Missing source sequence values were hidden or prevented a verified lifecycle match.");

        store = LifetimeStore(); store.ObserveBatch(info, [LifetimeEvent(1, ProcessEventKind.Started, 1000)], 0);
        store.ObserveBatch(info with { ResyncVersion = 2, Flags = ProcessEventFrameFlags.ResyncRequired }, [LifetimeEvent(2, ProcessEventKind.Stopped, 2000)], 0);
        entry = LifetimeEntry(store.Values.Single());
        Require(entry.Paired && entry.DurationSeconds == 1 && entry.Issues.HasFlag(ProcessLifetimeIssues.Gap),
            "A resynchronization request was confused with a new capture or a different process lifetime.");
    }

    private static void LifetimeSequenceAndCounterReset()
    {
        var store = LifetimeStore(); var info = LifetimeInfo();
        store.ObserveBatch(info, [LifetimeEvent(10, ProcessEventKind.Started, 1000)], 0);
        store.ObserveBatch(info, [LifetimeEvent(9, ProcessEventKind.Stopped, 2000)], 0);
        var rows = store.Values.Select(LifetimeEntry).ToArray();
        Require(rows.Length == 2 && rows.Select(row => row.Segment).Distinct().Count() == 2
            && rows.All(row => !row.Paired && row.Issues.HasFlag(ProcessLifetimeIssues.SequenceReset)) && store.MissingSequences == 0,
            "A regressed source sequence was joined across its consistency boundary or became an unsigned gap overflow.");

        store = LifetimeStore();
        store.ObserveBatch(info with { QueueDropped = 5 }, [LifetimeEvent(1, ProcessEventKind.Started, 1000)], 0);
        store.ObserveBatch(info, [LifetimeEvent(2, ProcessEventKind.Stopped, 2000)], 0);
        rows = store.Values.Select(LifetimeEntry).ToArray();
        Require(rows.Length == 2 && rows.All(row => !row.Paired) && rows.Any(row => row.Issues.HasFlag(ProcessLifetimeIssues.CounterReset)),
            "Reset source counters were accepted as a continuous capture.");
    }

    private static void LifetimeDelayedEvents()
    {
        var store = LifetimeStore(); var info = LifetimeInfo();
        store.ObserveBatch(info, [LifetimeEvent(1, ProcessEventKind.Stopped, 6000), LifetimeEvent(2, ProcessEventKind.Started, 2000)], 0);
        var entry = LifetimeEntry(store.Values.Single());
        Require(entry.Paired && entry.DurationSeconds == 4 && entry.Issues.HasFlag(ProcessLifetimeIssues.Late) && store.Late > 0,
            "A late verified start was dropped or given a negative/arrival-time duration.");

        store = LifetimeStore();
        store.ObserveBatch(info, [LifetimeEvent(1, ProcessEventKind.Started, 1000), LifetimeEvent(2, ProcessEventKind.Started, 9000, pid: 101),
            LifetimeEvent(3, ProcessEventKind.Stopped, 5000)], 0);
        entry = store.Values.Select(LifetimeEntry).Single(row => row.Key?.Pid == 100);
        Require(entry.Paired && entry.DurationSeconds == 4 && entry.Issues.HasFlag(ProcessLifetimeIssues.Late)
            && !entry.Issues.HasFlag(ProcessLifetimeIssues.Conflict), "An event delayed behind another PID was incorrectly treated as conflicting lifecycle chronology.");
    }

    private static void LifetimeDuplicatesAndConflicts()
    {
        var store = LifetimeStore(); var info = LifetimeInfo();
        var start = LifetimeEvent(1, ProcessEventKind.Started, 1000);
        store.ObserveBatch(info, [start], 0); var held = store.Values.Single();
        store.ObserveBatch(info, [start with { Sequence = 2, ObservedAtNs = 2_000_000 }], 0);
        var stop = LifetimeEvent(3, ProcessEventKind.Stopped, 2500);
        store.ObserveBatch(info, [stop, stop with { Sequence = 4, ObservedAtNs = 4_000_000 }], 0);
        var row = store.Values.Single(); var entry = LifetimeEntry(row);
        Require(entry.Paired && entry.DurationSeconds == 1.5 && entry.Duplicates == 2
            && entry.Issues.HasFlag(ProcessLifetimeIssues.Duplicate) && !entry.Issues.HasFlag(ProcessLifetimeIssues.Conflict)
            && row.SelectionIdentity == held.SelectionIdentity && LifetimeEntry(held).Duplicates == 0 && LifetimeEntry(held).Stop is null,
            "Duplicate endpoints inflated the lifetime list, changed source duration or mutated a held row.");
        Require(!entry.Stop!.Value.Event.Flags.HasFlag(ProcessEventFlags.HasExitStatus)
            && !row.Detail.Contains("0x00000000", StringComparison.Ordinal), "An unavailable exit status became an invented zero.");
        store.ObserveBatch(info, [LifetimeEvent(5, ProcessEventKind.Started, 1200), LifetimeEvent(6, ProcessEventKind.Stopped, 2700)], 0);
        var conflicted = store.Values.Select(LifetimeEntry).ToArray();
        Require(conflicted.Length >= 2 && conflicted.All(item => item.Issues.HasFlag(ProcessLifetimeIssues.Conflict)
            && item.DurationSeconds is null && !item.Paired) && store.Conflicts > 0,
            "Conflicting endpoints selected a plausible start/end or restored a falsely precise duration.");
    }

    private static void LifetimeQpcAvailability()
    {
        var store = LifetimeStore();
        store.ObserveBatch(LifetimeInfo(0), [LifetimeEvent(1, ProcessEventKind.Started, 1000), LifetimeEvent(2, ProcessEventKind.Stopped, 2000)], 0);
        var row = store.Values.Single();
        Require(LifetimeEntry(row).Paired && LifetimeEntry(row).DurationSeconds is null && row.DurationText == "Délka neurčena"
            && row.StartedText.Contains("nedostupný", StringComparison.Ordinal), "Missing QPC frequency became a numeric duration or timestamp.");

        store = LifetimeStore(); var info = LifetimeInfo();
        store.ObserveBatch(info, [LifetimeEvent(1, ProcessEventKind.Started, 1000)], 1000);
        store.ObserveBatch(LifetimeInfo(2000), [], 1000);
        store.ObserveBatch(LifetimeInfo(2000), [LifetimeEvent(2, ProcessEventKind.Stopped, 7000)], 1000);
        var rows = store.Values.Select(LifetimeEntry).ToArray();
        Require(rows.Length == 2 && rows.All(item => !item.Paired && item.DurationSeconds is null)
            && rows.Any(item => item.Issues.HasFlag(ProcessLifetimeIssues.ClockChanged))
            && rows.Single(item => item.Stop is not null).Stop!.Value.Origin == 7000,
            "A QPC frequency change in an empty batch reused an old clock origin or joined incomparable endpoints.");

        store = LifetimeStore();
        store.ObserveBatch(info, [LifetimeEvent(1, ProcessEventKind.Started, 5000), LifetimeEvent(2, ProcessEventKind.Stopped, 1000)], 0);
        var backwards = LifetimeEntry(store.Values.Single());
        Require(backwards.DurationSeconds is null && backwards.Issues.HasFlag(ProcessLifetimeIssues.Conflict),
            "An end before the verified start became a negative or absolute-value lifetime.");
        store = LifetimeStore();
        store.ObserveBatch(info, [LifetimeEvent(1, ProcessEventKind.Started, 3000), LifetimeEvent(2, ProcessEventKind.Stopped, 3000)], 0);
        Require(LifetimeEntry(store.Values.Single()).DurationSeconds == 0, "Equal measured QPC timestamps lost a genuine zero-length interval.");
    }

    private static void LifetimeStoppedCapture()
    {
        var store = LifetimeStore(); var info = LifetimeInfo();
        store.ObserveBatch(info, [LifetimeEvent(1, ProcessEventKind.Started, 1000)], 0);
        var held = store.Values.Single();
        Require(store.ObserveBatch(info with { State = ProcessEventMonitorState.Stopped }, [], 0), "Stopped capture did not refresh an open lifecycle state.");
        var row = store.Values.Single(); var entry = LifetimeEntry(row);
        Require(entry.Stop is null && entry.DurationSeconds is null && entry.CaptureState == ProcessEventMonitorState.Stopped
            && row.SelectionIdentity == held.SelectionIdentity && LifetimeEntry(held).CaptureState == ProcessEventMonitorState.Running,
            "Stopping capture invented a process stop, lost selection or changed a previously held row.");
    }

    private static void LifetimeBoundedTrim()
    {
        var store = LifetimeStore(); var info = LifetimeInfo(); string name = new('x', 1024);
        const int total = ProcessLifetimeStore.MaximumRows + 256;
        var first = LifetimeEvent(1, ProcessEventKind.Started, 1010, pid: 1001, generation: 10001, name: name);
        store.ObserveBatch(info, [first], 0); var held = store.Values.Single();
        for (int offset = 2; offset <= total; offset += 512) {
            var batch = Enumerable.Range(offset, Math.Min(512, total - offset + 1)).Select(value =>
                LifetimeEvent((ulong)value, ProcessEventKind.Started, 1000 + value * 10L, (uint)(1000 + value), (ulong)(10000 + value), name)).ToArray();
            store.ObserveBatch(info, batch, 0);
            Require(store.Count <= ProcessLifetimeStore.MaximumRows && store.EstimatedBytes <= ProcessLifetimeStore.MaximumEstimatedBytes,
                "Lifecycle retention exceeded its row or estimated byte budget while accepting valid maximum-length names.");
        }
        Require(store.Trimmed > 0 && store.Trimmed + (ulong)store.Count == total
            && store.Values.All(row => LifetimeEntry(row).Key?.Pid != first.Pid)
            && store.Values.Any(row => LifetimeEntry(row).Key?.Pid == 1000 + total),
            "Bounded lifecycle retention kept an evicted endpoint, lost new data or reported inaccurate trim counts.");
        var stopped = info with { ProviderEventsLost = 1, State = ProcessEventMonitorState.Stopped };
        store.ObserveBatch(stopped, [], 0);
        Require(store.EstimatedBytes <= ProcessLifetimeStore.MaximumEstimatedBytes && store.Count <= ProcessLifetimeStore.MaximumRows,
            "Updating gap and capture-state labels without new events exceeded the lifecycle memory budget.");
        store.ObserveBatch(stopped, [first with { Sequence = total + 1, Kind = ProcessEventKind.Stopped, TimestampQpc = 100000,
            ObservedAtNs = (total + 1L) * 1_000_000 }], 0);
        var reappeared = store.Values.Select(LifetimeEntry).Single(item => item.Key?.Pid == first.Pid);
        Require(reappeared.Start is null && reappeared.Stop is not null && reappeared.DurationSeconds is null
            && reappeared.Issues.HasFlag(ProcessLifetimeIssues.Trimmed) && LifetimeEntry(held).Stop is null,
            "An evicted start survived in the join index or a retained historical row was mutated after eviction.");
    }
}
