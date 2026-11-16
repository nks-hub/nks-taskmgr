using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static readonly BootIdentifier TimelineBoot = new(1, 2);
    private const long TimelineMonotonicNs = 10_000_000_000;

    private static ProcessSeen TimelineSeen(uint pid, uint parent, string name, DateTime created)
    {
        ulong stamp = (ulong)created.ToFileTimeUtc();
        return new(new ProcessKey(TimelineBoot, pid, GenerationKind.CreationTime, stamp), parent, name, stamp);
    }

    private static ProcessEventInfo TimelineEvent(ProcessEventKind kind, uint pid, uint parent, DateTime created, long receivedAgoMs, int exit = 0, string name = "ping.exe")
        => new(1, 0, TimelineMonotonicNs - receivedAgoMs * 1_000_000, kind, pid, parent, SampleState.Valid, GenerationKind.CreationTime, (ulong)created.ToFileTimeUtc(),
            ProcessEventFlags.HasParentPid | ProcessEventFlags.HasImageName | (kind == ProcessEventKind.Stopped ? ProcessEventFlags.HasExitStatus : 0), exit, name);

    private static void ProcessTimelineLifetimes()
    {
        var now = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Local);
        var reads = new List<ProcessKey>();
        var timeline = new ProcessTimeline(key => { reads.Add(key); return "cmd /c " + key.Pid; }, () => now);
        var explorer = TimelineSeen(100, 4, "explorer.exe", now.AddHours(-1));

        timeline.Observe([explorer]);
        Require(timeline.Entries.Count == 0 && reads.Count == 1, "The processes already running at the first snapshot were listed as new starts.");

        now = now.AddSeconds(1);
        var cmd = TimelineSeen(200, 100, "cmd.exe", now);
        timeline.Observe([explorer, cmd]);
        var entry = timeline.Entries.Single();
        Require(entry.Name == "cmd.exe" && entry.ParentName == "explorer.exe" && entry.ParentPid == 100 && entry.Started == now && entry.Alive
            && entry.CommandLine == "cmd /c 200" && timeline.IsAlive(cmd.Key), "A started process lost its parent name, start time or command line.");

        now = now.AddSeconds(3);
        timeline.Observe([explorer]);
        Require(entry.Ended == now && entry.Duration == TimeSpan.FromSeconds(3) && entry.ExitCode is null && timeline.Entries.Count == 1 && !timeline.IsAlive(cmd.Key),
            "An ended process was not paired with its start or invented an exit code.");

        now = now.AddSeconds(1);
        timeline.Observe([]);
        var preexisting = timeline.Entries.Single(item => item.Pid == 100);
        Require(timeline.Entries.Count == 2 && preexisting.Preexisting && preexisting.Duration == TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5)
            && preexisting.CommandLine == "cmd /c 100", "A process that ended after running since before the application started was not listed with its real duration.");

        // Respawn: the same image and parent three or more times within 60 s.
        var starts = new List<ProcessTimelineEntry>();
        void Spawn(uint pid, uint parent, int afterSeconds, string name = "worker.exe")
        {
            now = now.AddSeconds(afterSeconds);
            var seen = TimelineSeen(pid, parent, name, now);
            timeline.Observe([seen]); starts.Add(timeline.Entries.Single(item => item.Pid == pid)); timeline.Observe([]);
        }
        Spawn(301, 300, 5); Spawn(302, 300, 10);
        Require(starts.All(item => item.Respawn == 0), "Two starts were marked as repeated.");
        Spawn(303, 300, 10);
        Require(starts.All(item => item.Respawn == 3), "Three starts within 60 s were not all marked as repeated.");
        Spawn(304, 301, 5, "worker.exe");
        Require(starts[3].Respawn == 0, "A start with another parent counted as repeated.");
        Spawn(305, 300, 15);
        Require(starts[4].Respawn == 4 && starts[0].Respawn == 4 && starts[2].Respawn == 4, "A fourth start within the window did not raise the whole group.");
        Spawn(306, 300, 200);
        Require(starts[5].Respawn == 0, "A start long after the others was marked as repeated.");

        // ETW adds the exit code to a process the snapshots saw, and short processes the snapshots missed.
        now = now.AddSeconds(10);
        var created400 = now.AddSeconds(-5);
        timeline.Observe([TimelineSeen(400, 100, "ping.exe", created400)]);
        var known = timeline.Entries.Single(item => item.Pid == 400);
        timeline.ObserveEvents([TimelineEvent(ProcessEventKind.Stopped, 400, 100, created400, 1000, 42)], TimelineMonotonicNs);
        Require(known.ExitCode == 42 && known.Ended == now.AddSeconds(-1) && !known.SnapshotsMissed && timeline.Entries.Count(item => item.Pid == 400) == 1,
            "An ETW stop did not add its exit code to the matching process.");

        var shortLived = now.AddSeconds(-1);
        timeline.ObserveEvents([TimelineEvent(ProcessEventKind.Started, 500, 100, shortLived, 300), TimelineEvent(ProcessEventKind.Stopped, 500, 100, shortLived, 100, 3)], TimelineMonotonicNs);
        var missed = timeline.Entries.Single(item => item.Pid == 500);
        Require(missed.SnapshotsMissed && missed.ExitCode == 3 && missed.ParentName == "explorer.exe" && !missed.Alive && missed.Name == "ping.exe",
            "A short process known only from ETW was not added with its parent name and exit code.");
        var rows = ProcessEventRow.Build(timeline.Entries, ProcessEventFilter.Ended, "", new());
        Require(rows.Single(row => row.Entry == missed).NameText.Contains(Loc.T("krátký proces"), StringComparison.Ordinal)
            && rows.Single(row => row.Entry == missed).ExitText == "3", "A process missed by the snapshots was not marked as such.");

        var late = now.AddSeconds(-1);
        timeline.ObserveEvents([TimelineEvent(ProcessEventKind.Started, 600, 100, late, 200)], TimelineMonotonicNs);
        var etwFirst = timeline.Entries.Single(item => item.Pid == 600);
        timeline.Observe([TimelineSeen(600, 100, "ping.exe", late)]);
        Require(timeline.Entries.Count(item => item.Pid == 600) == 1 && !etwFirst.SnapshotsMissed && etwFirst.Key is not null && etwFirst.CommandLine == "cmd /c 600",
            "A process seen by ETW and then by a snapshot was listed twice or stayed marked as missed.");

        int before = timeline.Entries.Count;
        timeline.ObserveEvents([TimelineEvent(ProcessEventKind.Started, 700, 100, timeline.ObservingSince.AddHours(-3), 100)], TimelineMonotonicNs);
        Require(timeline.Entries.Count == before, "An ETW start of a process older than the timeline was listed as a new start.");

        ProcessTimelineRowsAndLimits();
        App.Trace("Process timeline checks passed: pairing, parent names, preexisting processes, respawn, ETW exit codes and short processes, rows, read and size limits");
    }

    private static void ProcessTimelineRowsAndLimits()
    {
        var now = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Local);
        var timeline = new ProcessTimeline(_ => "", () => now);
        var system = TimelineSeen(1, 0, "System", now.AddDays(-1));
        timeline.Observe([system]);
        now = now.AddSeconds(1);
        timeline.Observe([system, TimelineSeen(10, 1, "a.exe", now)]);
        now = now.AddSeconds(2);
        timeline.Observe([system]);
        var cache = new Dictionary<(ProcessTimelineEntry, bool), ProcessEventRow>();
        var all = ProcessEventRow.Build(timeline.Entries, ProcessEventFilter.All, "", cache);
        Require(all.Count == 2 && all[0].EventText == Loc.T("Ukončen") && all[1].EventText == Loc.T("Spuštěn") && all[0].TimeText == "08:00:03" && all[1].TimeText == "08:00:01"
            && all[0].DurationText == Loc.F("{0} s", 2d.ToString("0.0", Loc.Culture)) && all[0].ExitText == "—" && all[1].DurationText.Length == 0, "Timeline rows were not newest first or lacked clock time, duration or exit code text.");
        Require(ProcessEventRow.Build(timeline.Entries, ProcessEventFilter.Started, "", cache).Count == 1 && ProcessEventRow.Build(timeline.Entries, ProcessEventFilter.Ended, "", cache).Count == 1
            && ProcessEventRow.Build(timeline.Entries, ProcessEventFilter.Repeated, "", cache).Count == 0 && ProcessEventRow.Build(timeline.Entries, ProcessEventFilter.All, "A.EX", cache).Count == 2
            && ProcessEventRow.Build(timeline.Entries, ProcessEventFilter.All, "nothing", cache).Count == 0 && ProcessEventRow.Build(timeline.Entries, ProcessEventFilter.All, "10", cache).Count == 2,
            "A timeline filter or search returned the wrong rows.");
        Require(ProcessEventRow.ExitCode(0) == "0" && ProcessEventRow.ExitCode(3) == "3" && ProcessEventRow.ExitCode(unchecked((int)0xC0000005)) == "0xC0000005", "Exit codes were formatted incorrectly.");

        var reads = 0;
        var many = new ProcessTimeline(_ => { ++reads; return "x"; }, () => now);
        var running = Enumerable.Range(1, 120).Select(pid => TimelineSeen((uint)pid, 0, "p" + pid, now.AddDays(-1))).ToArray();
        many.Observe(running);
        Require(reads == ProcessTimeline.ReadsPerFrame, "One frame read more command lines than the per-frame limit.");
        many.Observe(running); many.Observe(running);
        Require(reads == 120, "Pending command lines were not read over the following frames.");

        var capped = new ProcessTimeline(_ => "", () => now);
        capped.Observe([]);
        for (uint i = 1; i <= ProcessTimeline.MaximumEntries + 100; ++i) {
            now = now.AddMilliseconds(10);
            capped.Observe([TimelineSeen(i, 0, "n" + i, now)]); capped.Observe([]);
        }
        Require(capped.Entries.Count == ProcessTimeline.MaximumEntries && capped.Entries[0].Pid == 101 && capped.Entries[^1].Pid == ProcessTimeline.MaximumEntries + 100,
            "The timeline did not drop its oldest entries at the size limit.");
    }
}
