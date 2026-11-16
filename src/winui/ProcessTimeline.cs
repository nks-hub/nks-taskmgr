using System.Collections.Concurrent;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

/// <summary>One process lifetime of the timeline. Times are local wall-clock time.</summary>
internal sealed class ProcessTimelineEntry
{
    internal ProcessKey? Key { get; set; }
    internal uint Pid { get; init; }
    internal ulong Created100ns { get; init; }
    internal string Name { get; set; } = "";
    internal string ParentName { get; set; } = "";
    internal string CommandLine { get; set; } = "";
    internal uint ParentPid { get; set; }
    internal DateTime Started { get; init; }
    internal DateTime? Ended { get; set; }
    /// <summary>Set when snapshots were missing before the end was noticed: the process ended between this time and <see cref="Ended"/>.</summary>
    internal DateTime? EndedAfter { get; set; }
    internal int? ExitCode { get; set; }
    /// <summary>Already running when the application started; listed only once it ends.</summary>
    internal bool Preexisting { get; init; }
    /// <summary>Known from ETW only; no process snapshot has contained it.</summary>
    internal bool SnapshotsMissed { get; set; }
    internal bool Listed { get; set; }
    internal int Respawn { get; set; }
    internal int Revision { get; set; }
    internal bool Alive => Ended is null;
    internal TimeSpan? Duration => Ended is { } ended ? ended > Started ? ended - Started : TimeSpan.Zero : null;
}

/// <summary>A process as seen by one snapshot of the process table.</summary>
internal readonly record struct ProcessSeen(ProcessKey Key, uint ParentPid, string Name, ulong Created100ns);

/// <summary>
/// Bounded list of process starts and ends since the application started. Process snapshots supply every entry; an ETW capture,
/// when it runs, adds exit codes and the short-lived processes the snapshots missed. Entries pair by PID plus creation time.
/// </summary>
internal sealed class ProcessTimeline
{
    internal const int MaximumEntries = 2000, ReadsPerFrame = 50, RespawnThreshold = 3;
    internal static readonly TimeSpan RespawnWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StaleGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RundownSlack = TimeSpan.FromSeconds(2);
    private readonly List<ProcessTimelineEntry> entries = [];
    private readonly Dictionary<ProcessKey, ProcessTimelineEntry> alive = [];
    private readonly Dictionary<(uint Pid, ulong Created), ProcessTimelineEntry> byIdentity = [];
    private readonly Dictionary<uint, (ulong Created, string Name)> lastNames = [];
    private readonly Dictionary<uint, ProcessSeen> current = [];
    private readonly HashSet<ProcessKey> present = [];
    private readonly List<ProcessKey> removed = [];
    private readonly Queue<ProcessTimelineEntry> freshReads = new(), olderReads = new();
    private readonly Func<ProcessKey, string> readCommandLine;
    private readonly Func<DateTime> clock;
    private readonly bool backgroundReads;
    private readonly ConcurrentQueue<(ProcessTimelineEntry Entry, string Text)> readResults = new();
    private Task? olderReadTask;
    private DateTime? lastSnapshot;
    private bool seeded;
    private long lastProcessTime;

    internal ProcessTimeline(Func<ProcessKey, string>? readCommandLine = null, Func<DateTime>? clock = null)
    {
        // Production reads the command lines of processes that were already running off the UI thread; injected readers stay synchronous for checks.
        backgroundReads = readCommandLine is null;
        this.readCommandLine = readCommandLine ?? ProcessFacts.ReadCommandLine;
        this.clock = clock ?? (() => DateTime.Now);
        ObservingSince = this.clock();
    }

    internal DateTime ObservingSince { get; }
    /// <summary>Changes with every added or updated entry; views compare it to know when to rebuild.</summary>
    internal int Version { get; private set; }
    internal IReadOnlyList<ProcessTimelineEntry> Entries => entries;
    internal bool IsAlive(ProcessKey key) => alive.TryGetValue(key, out var entry) && entry.Alive;

    /// <summary>Feeds the model's current process list once per new process frame.</summary>
    internal void Observe(MonitorModel model)
    {
        if (model.ProcessTime == lastProcessTime || model.Processes.Count == 0) return;
        lastProcessTime = model.ProcessTime;
        DateTime? taken = model.ProcessWallTimeUnixNs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(model.ProcessWallTimeUnixNs / 1_000_000).LocalDateTime : null;
        Observe(model.Processes.Select(row => new ProcessSeen(row.Key, row.ParentPid, row.Name, row.Created100ns)).ToArray(), taken);
    }

    /// <summary>Compares one snapshot with the previous one; the first call only records the processes already running.
    /// <paramref name="taken"/> is when the snapshot was taken; a process missing from it ended after the previous snapshot.</summary>
    internal void Observe(IReadOnlyCollection<ProcessSeen> seen, DateTime? taken = null)
    {
        var now = taken ?? clock();
        var previous = lastSnapshot; lastSnapshot = now;
        present.Clear(); current.Clear();
        foreach (var item in seen) { present.Add(item.Key); current[item.Key.Pid] = item; }
        foreach (var item in seen) {
            if (alive.ContainsKey(item.Key)) continue;
            var entry = Adopt(item) ?? new ProcessTimelineEntry {
                Pid = item.Key.Pid, Created100ns = item.Created100ns, Name = item.Name, ParentPid = item.ParentPid,
                Started = StartTime(item.Created100ns, now), Preexisting = !seeded, ParentName = ParentName(item.ParentPid, item.Created100ns)
            };
            entry.Key = item.Key; alive[item.Key] = entry;
            if (entry.Created100ns != 0) byIdentity[(entry.Pid, entry.Created100ns)] = entry;
            if (!entry.Preexisting && !entry.Listed) List(entry);
            (entry.Preexisting ? olderReads : freshReads).Enqueue(entry);
            Touch(entry);
        }
        removed.Clear();
        foreach (var (key, _) in alive) if (!present.Contains(key)) removed.Add(key);
        foreach (var key in removed) {
            var entry = alive[key]; alive.Remove(key);
            if (entry.Ended is null && previous is { } before && now - before > StaleGap) entry.EndedAfter = before;
            Finish(entry, now);
        }
        foreach (var item in seen) lastNames[item.Key.Pid] = (item.Created100ns, item.Name);
        if (lastNames.Count > 20000) lastNames.Clear();
        seeded = true;
        while (readResults.TryDequeue(out var result))
            if (result.Text.Length > 0 && result.Text != result.Entry.CommandLine) { result.Entry.CommandLine = result.Text; Touch(result.Entry); }
        if (backgroundReads && olderReads.Count > 0 && olderReadTask is not { IsCompleted: false }) {
            var batch = olderReads.Where(entry => entry.Key is not null && entry.Ended is null).Select(entry => (Entry: entry, Key: entry.Key!.Value)).ToArray();
            olderReads.Clear();
            olderReadTask = Task.Run(() => { foreach (var item in batch) readResults.Enqueue((item.Entry, readCommandLine(item.Key))); });
        }
        for (int budget = ReadsPerFrame; budget > 0 && (freshReads.Count > 0 || olderReads.Count > 0);) {
            var entry = freshReads.Count > 0 ? freshReads.Dequeue() : olderReads.Dequeue();
            if (entry.Key is not { } key || !present.Contains(key) || entry.Ended is not null) continue;
            --budget;
            string text = readCommandLine(key);
            if (text.Length > 0 && text != entry.CommandLine) { entry.CommandLine = text; Touch(entry); }
        }
    }

    /// <summary>Merges ETW start and stop events. An event's own QPC time gives its wall-clock time when <paramref name="nowQpc"/> and
    /// <paramref name="frequency"/> are known; otherwise the receive time relative to <paramref name="monotonicNowNs"/> stands in.</summary>
    internal void ObserveEvents(IEnumerable<ProcessEventInfo> events, long monotonicNowNs, long nowQpc = 0, long frequency = 0)
    {
        var now = clock();
        foreach (var item in events) {
            bool identified = item.IdentityState == SampleState.Valid && item.IdentityKind == GenerationKind.CreationTime && item.Generation != 0 && item.Pid != 0;
            if (!identified) continue;
            long agoTicks = frequency > 0 && nowQpc > 0 && item.TimestampQpc > 0 && item.TimestampQpc <= nowQpc
                ? (long)((nowQpc - item.TimestampQpc) * (10_000_000d / frequency)) : (monotonicNowNs - item.ObservedAtNs) / 100;
            var when = now - TimeSpan.FromTicks(Math.Max(0, agoTicks));
            byIdentity.TryGetValue((item.Pid, item.Generation), out var entry);
            bool hasParent = (item.Flags & ProcessEventFlags.HasParentPid) != 0;
            if (item.Kind == ProcessEventKind.Started) {
                var started = StartTime(item.Generation, now);
                if (entry is not null || started < ObservingSince - RundownSlack) continue;
                entry = new ProcessTimelineEntry {
                    Pid = item.Pid, Created100ns = item.Generation, Name = ImageName(item.ImageName), Started = started, SnapshotsMissed = true,
                    ParentPid = hasParent ? item.ParentPid : 0, ParentName = hasParent ? ParentName(item.ParentPid, item.Generation) : ""
                };
                byIdentity[(entry.Pid, entry.Created100ns)] = entry; List(entry); Touch(entry);
                continue;
            }
            if (entry is null) {
                var started = StartTime(item.Generation, now);
                entry = new ProcessTimelineEntry {
                    Pid = item.Pid, Created100ns = item.Generation, Name = ImageName(item.ImageName), Started = started, Preexisting = started < ObservingSince - RundownSlack,
                    SnapshotsMissed = started >= ObservingSince - RundownSlack, ParentPid = hasParent ? item.ParentPid : 0,
                    ParentName = hasParent ? ParentName(item.ParentPid, item.Generation) : ""
                };
                byIdentity[(entry.Pid, entry.Created100ns)] = entry;
                if (!entry.Preexisting) List(entry);
            }
            if ((item.Flags & ProcessEventFlags.HasExitStatus) != 0) entry.ExitCode = item.ExitStatus;
            if (entry.Name.Length == 0) entry.Name = ImageName(item.ImageName);
            Finish(entry, when > entry.Started ? when : entry.Started, true);
        }
    }

    private ProcessTimelineEntry? Adopt(ProcessSeen item)
    {
        if (item.Created100ns == 0 || !byIdentity.TryGetValue((item.Key.Pid, item.Created100ns), out var entry) || entry.Key is not null) return null;
        entry.SnapshotsMissed = false;
        // The snapshot's image name is the one every other view shows.
        if (item.Name.Length > 0) entry.Name = item.Name;
        if (entry.ParentPid == 0) entry.ParentPid = item.ParentPid;
        if (entry.ParentName.Length == 0) entry.ParentName = ParentName(entry.ParentPid, item.Created100ns);
        return entry;
    }

    private void Finish(ProcessTimelineEntry entry, DateTime ended, bool fromEvent = false)
    {
        if (entry.Ended is null || fromEvent) entry.Ended = ended;
        if (fromEvent) entry.EndedAfter = null;
        if (!entry.Listed) List(entry);
        Touch(entry);
    }

    private void List(ProcessTimelineEntry entry)
    {
        entry.Listed = true; entries.Add(entry);
        // Only starts seen while the application watched count; processes running before it keep their old start time.
        if (entry.Name.Length > 0 && entry.ParentPid != 0 && !entry.Preexisting) {
            // Snapshots list simultaneous starts in any order, so candidates come from both sides and are sorted before the 60 s window slides.
            var group = entries.Where(other => !other.Preexisting && other.ParentPid == entry.ParentPid && string.Equals(other.Name, entry.Name, StringComparison.OrdinalIgnoreCase)
                && (entry.Started - other.Started).Duration() <= RespawnWindow).OrderBy(other => other.Started).ToList();
            for (int last = 0, first = 0; last < group.Count; ++last) {
                while (group[last].Started - group[first].Started > RespawnWindow) ++first;
                int size = last - first + 1;
                if (size < RespawnThreshold) continue;
                for (int i = first; i <= last; ++i) if (group[i].Respawn < size) { group[i].Respawn = size; ++group[i].Revision; }
            }
        }
        while (entries.Count > MaximumEntries) {
            var oldest = entries[0]; entries.RemoveAt(0); oldest.Listed = false;
            // A running process keeps its identity so its end is still paired and listed again.
            if (oldest.Alive) continue;
            if (byIdentity.TryGetValue((oldest.Pid, oldest.Created100ns), out var kept) && ReferenceEquals(kept, oldest)) byIdentity.Remove((oldest.Pid, oldest.Created100ns));
        }
    }

    private void Touch(ProcessTimelineEntry entry) { ++entry.Revision; ++Version; }

    private string ParentName(uint parentPid, ulong childCreated)
    {
        if (parentPid == 0) return "";
        // A parent created after its child is a reused PID, not the real parent.
        if (current.TryGetValue(parentPid, out var parent) && (childCreated == 0 || parent.Created100ns == 0 || parent.Created100ns <= childCreated)) return parent.Name;
        if (lastNames.TryGetValue(parentPid, out var known) && (childCreated == 0 || known.Created == 0 || known.Created <= childCreated)) return known.Name;
        // A parent that started after the last snapshot is known only from its own ETW start.
        for (int i = entries.Count - 1; i >= 0; --i) {
            var other = entries[i];
            if (other.Pid == parentPid && other.Name.Length > 0 && (childCreated == 0 || other.Created100ns == 0 || other.Created100ns <= childCreated)) return other.Name;
        }
        return "";
    }

    /// <summary>ETW reports the image as an NT device path; the timeline shows the file name like the process table.</summary>
    private static string ImageName(string raw) => raw.Length == 0 ? raw : Path.GetFileName(raw);

    private static DateTime StartTime(ulong created100ns, DateTime fallback)
        => created100ns != 0 && created100ns <= (ulong)DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc((long)created100ns).ToLocalTime() : fallback;
}
