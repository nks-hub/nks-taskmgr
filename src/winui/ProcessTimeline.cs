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
    private bool seeded;
    private long lastProcessTime;

    internal ProcessTimeline(Func<ProcessKey, string>? readCommandLine = null, Func<DateTime>? clock = null)
    {
        this.readCommandLine = readCommandLine ?? ProcessFacts.ReadCommandLine;
        this.clock = clock ?? (() => DateTime.Now);
        ObservingSince = this.clock();
    }

    internal DateTime ObservingSince { get; }
    /// <summary>Changes with every added or updated entry; views compare it to know when to rebuild.</summary>
    internal int Version { get; private set; }
    /// <summary>True while an ETW capture delivers start and stop events.</summary>
    internal bool EtwActive { get; set; }
    internal IReadOnlyList<ProcessTimelineEntry> Entries => entries;
    internal bool IsAlive(ProcessKey key) => alive.TryGetValue(key, out var entry) && entry.Alive;

    /// <summary>Feeds the model's current process list once per new process frame.</summary>
    internal void Observe(MonitorModel model)
    {
        if (model.ProcessTime == lastProcessTime || model.Processes.Count == 0) return;
        lastProcessTime = model.ProcessTime;
        Observe(model.Processes.Select(row => new ProcessSeen(row.Key, row.ParentPid, row.Name, row.Created100ns)).ToArray());
    }

    /// <summary>Compares one snapshot with the previous one; the first call only records the processes already running.</summary>
    internal void Observe(IReadOnlyCollection<ProcessSeen> seen)
    {
        var now = clock();
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
        foreach (var key in removed) { var entry = alive[key]; alive.Remove(key); Finish(entry, now); }
        foreach (var item in seen) lastNames[item.Key.Pid] = (item.Created100ns, item.Name);
        if (lastNames.Count > 20000) lastNames.Clear();
        seeded = true;
        for (int budget = ReadsPerFrame; budget > 0 && (freshReads.Count > 0 || olderReads.Count > 0);) {
            var entry = freshReads.Count > 0 ? freshReads.Dequeue() : olderReads.Dequeue();
            if (entry.Key is not { } key || !present.Contains(key) || entry.Ended is not null) continue;
            --budget;
            string text = readCommandLine(key);
            if (text.Length > 0 && text != entry.CommandLine) { entry.CommandLine = text; Touch(entry); }
        }
    }

    /// <summary>Merges ETW start and stop events; <paramref name="monotonicNowNs"/> converts their receive time to wall-clock time.</summary>
    internal void ObserveEvents(IEnumerable<ProcessEventInfo> events, long monotonicNowNs)
    {
        var now = clock();
        foreach (var item in events) {
            bool identified = item.IdentityState == SampleState.Valid && item.IdentityKind == GenerationKind.CreationTime && item.Generation != 0 && item.Pid != 0;
            if (!identified) continue;
            var when = now - TimeSpan.FromTicks(Math.Max(0, (monotonicNowNs - item.ObservedAtNs) / 100));
            byIdentity.TryGetValue((item.Pid, item.Generation), out var entry);
            bool hasParent = (item.Flags & ProcessEventFlags.HasParentPid) != 0;
            if (item.Kind == ProcessEventKind.Started) {
                var started = StartTime(item.Generation, now);
                if (entry is not null || started < ObservingSince - RundownSlack) continue;
                entry = new ProcessTimelineEntry {
                    Pid = item.Pid, Created100ns = item.Generation, Name = item.ImageName, Started = started, SnapshotsMissed = true,
                    ParentPid = hasParent ? item.ParentPid : 0, ParentName = hasParent ? ParentName(item.ParentPid, item.Generation) : ""
                };
                byIdentity[(entry.Pid, entry.Created100ns)] = entry; List(entry); Touch(entry);
                continue;
            }
            if (entry is null) {
                var started = StartTime(item.Generation, now);
                entry = new ProcessTimelineEntry {
                    Pid = item.Pid, Created100ns = item.Generation, Name = item.ImageName, Started = started, Preexisting = started < ObservingSince - RundownSlack,
                    SnapshotsMissed = started >= ObservingSince - RundownSlack, ParentPid = hasParent ? item.ParentPid : 0,
                    ParentName = hasParent ? ParentName(item.ParentPid, item.Generation) : ""
                };
                byIdentity[(entry.Pid, entry.Created100ns)] = entry;
                if (!entry.Preexisting) List(entry);
            }
            if ((item.Flags & ProcessEventFlags.HasExitStatus) != 0) entry.ExitCode = item.ExitStatus;
            if (entry.Name.Length == 0) entry.Name = item.ImageName;
            Finish(entry, when > entry.Started ? when : entry.Started, true);
        }
    }

    private ProcessTimelineEntry? Adopt(ProcessSeen item)
    {
        if (item.Created100ns == 0 || !byIdentity.TryGetValue((item.Key.Pid, item.Created100ns), out var entry) || entry.Key is not null) return null;
        entry.SnapshotsMissed = false;
        if (entry.Name.Length == 0) entry.Name = item.Name;
        if (entry.ParentPid == 0) { entry.ParentPid = item.ParentPid; entry.ParentName = ParentName(item.ParentPid, item.Created100ns); }
        return entry;
    }

    private void Finish(ProcessTimelineEntry entry, DateTime ended, bool fromEvent = false)
    {
        if (entry.Ended is null || fromEvent) entry.Ended = ended;
        if (!entry.Listed) List(entry);
        Touch(entry);
    }

    private void List(ProcessTimelineEntry entry)
    {
        entry.Listed = true; entries.Add(entry);
        if (entry.Name.Length > 0 && entry.ParentPid != 0) {
            // Snapshots list simultaneous starts in any order, so the distance counts both ways.
            bool Together(ProcessTimelineEntry other) => (entry.Started - other.Started).Duration() <= RespawnWindow;
            var group = entries.Where(other => other.ParentPid == entry.ParentPid && string.Equals(other.Name, entry.Name, StringComparison.OrdinalIgnoreCase) && Together(other)).ToList();
            if (group.Count >= RespawnThreshold) foreach (var other in group) { other.Respawn = Math.Max(other.Respawn, group.Count); ++other.Revision; }
        }
        while (entries.Count > MaximumEntries) {
            var oldest = entries[0]; entries.RemoveAt(0);
            if (byIdentity.TryGetValue((oldest.Pid, oldest.Created100ns), out var kept) && ReferenceEquals(kept, oldest)) byIdentity.Remove((oldest.Pid, oldest.Created100ns));
        }
    }

    private void Touch(ProcessTimelineEntry entry) { ++entry.Revision; ++Version; }

    private string ParentName(uint parentPid, ulong childCreated)
    {
        if (parentPid == 0) return "";
        // A parent created after its child is a reused PID, not the real parent.
        if (current.TryGetValue(parentPid, out var parent) && (childCreated == 0 || parent.Created100ns == 0 || parent.Created100ns <= childCreated)) return parent.Name;
        return lastNames.TryGetValue(parentPid, out var known) && (childCreated == 0 || known.Created == 0 || known.Created <= childCreated) ? known.Name : "";
    }

    private static DateTime StartTime(ulong created100ns, DateTime fallback)
        => created100ns != 0 && created100ns <= (ulong)DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc((long)created100ns).ToLocalTime() : fallback;
}
