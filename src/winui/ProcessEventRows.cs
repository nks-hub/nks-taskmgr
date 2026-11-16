using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;

namespace NksTaskMgr.WinUI;

/// <summary>Which rows of the timeline the window shows.</summary>
internal enum ProcessEventFilter { All, Started, Ended, Repeated }

/// <summary>One start or end of a process as a table row.</summary>
public sealed class ProcessEventRow
{
    internal ProcessEventRow(ProcessTimelineEntry entry, bool started)
    {
        Entry = entry; Started = started; Revision = entry.Revision;
        When = started ? entry.Started : entry.Ended ?? entry.Started;
        TimeText = When.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        EventText = started ? Loc.T("Spuštěn") : Loc.T("Ukončen");
        string name = entry.Name.Length == 0 ? Loc.T("neznámý proces") : entry.Name;
        NameText = entry.SnapshotsMissed && entry.Ended is not null ? name + " · " + Loc.T("krátký proces") : name;
        PidText = entry.Pid.ToString(CultureInfo.InvariantCulture);
        ParentText = entry.ParentPid == 0 ? "—" : entry.ParentName.Length > 0 ? entry.ParentName + " (" + entry.ParentPid.ToString(CultureInfo.InvariantCulture) + ")" : entry.ParentPid.ToString(CultureInfo.InvariantCulture);
        CommandText = entry.CommandLine.Length == 0 ? "—" : GlobalObjectSearchText.Flat(entry.CommandLine, 2048);
        DurationText = !started && entry.Duration is { } duration ? (entry.EndedAfter is null ? "" : "≤ ") + Duration(duration) : "";
        ExitText = started ? "" : entry.ExitCode is { } code ? ExitCode(code) : "—";
        RepeatText = entry.Respawn >= ProcessTimeline.RespawnThreshold ? "×" + entry.Respawn.ToString(CultureInfo.InvariantCulture) : "";
        Tooltip = Loc.F("Spuštěn {0}", entry.Started.ToString("G", Loc.Culture))
            + (entry.Ended is { } ended ? "\n" + (entry.EndedAfter is { } after
                ? Loc.F("Ukončen mezi {0} a {1}; seznam procesů se mezitím neobnovoval (pozastavení nebo zpoždění).", after.ToString("G", Loc.Culture), ended.ToString("G", Loc.Culture))
                : Loc.F("Ukončen {0}", ended.ToString("G", Loc.Culture))) : "")
            + (entry.Preexisting ? "\n" + Loc.T("Běžel už před spuštěním aplikace.") : "")
            + (entry.SnapshotsMissed && entry.Ended is not null ? "\n" + Loc.T("Skončil dřív, než ho stihl zachytit seznam procesů. Známe jen údaje ze sledování ETW, bez příkazového řádku.") : "");
    }

    internal ProcessTimelineEntry Entry { get; }
    internal bool Started { get; }
    internal int Revision { get; }
    internal DateTime When { get; }
    public string TimeText { get; }
    public string EventText { get; }
    public string NameText { get; }
    public string PidText { get; }
    public string ParentText { get; }
    public string CommandText { get; }
    public string DurationText { get; }
    public string ExitText { get; }
    public string RepeatText { get; }
    public string Tooltip { get; }

    internal bool Matches(string query) => NameText.Contains(query, StringComparison.OrdinalIgnoreCase) || PidText.Contains(query, StringComparison.Ordinal)
        || Entry.CommandLine.Contains(query, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => string.Join(", ", TimeText, EventText, NameText, "PID " + PidText, Loc.T("rodič") + " " + ParentText, CommandText, DurationText, ExitText, RepeatText);

    internal string Line => string.Join('\t', TimeText, EventText, Entry.Name, PidText, ParentText, Entry.CommandLine, DurationText, ExitText, RepeatText);

    internal static string Duration(TimeSpan value) => value.TotalSeconds < 60 ? Loc.F("{0} s", value.TotalSeconds.ToString(value.TotalSeconds < 10 ? "0.0" : "0", Loc.Culture))
        : value.TotalHours < 1 ? Loc.F("{0} min {1} s", (int)value.TotalMinutes, value.Seconds)
        : Loc.F("{0} h {1} min", (int)value.TotalHours, value.Minutes);

    /// <summary>Small codes in decimal; Windows status codes such as 0xC0000005 in hex.</summary>
    internal static string ExitCode(int code) => code is >= 0 and < 100_000 ? code.ToString(CultureInfo.InvariantCulture) : "0x" + unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture);

    private static readonly bool[] StartOnly = [true], EndOnly = [false], Both = [true, false];

    /// <summary>Starts and ends of the timeline matching the filter and query, newest first. Rows of unchanged entries are reused from <paramref name="cache"/>.</summary>
    internal static List<ProcessEventRow> Build(IEnumerable<ProcessTimelineEntry> entries, ProcessEventFilter filter, string query, Dictionary<(ProcessTimelineEntry, bool), ProcessEventRow> cache)
    {
        var rows = new List<ProcessEventRow>();
        foreach (var entry in entries) {
            if (filter == ProcessEventFilter.Repeated && entry.Respawn < ProcessTimeline.RespawnThreshold) continue;
            foreach (bool started in entry.Ended is null ? StartOnly : entry.Preexisting ? EndOnly : Both) {
                if (filter == ProcessEventFilter.Started && !started || filter == ProcessEventFilter.Ended && started) continue;
                if (!cache.TryGetValue((entry, started), out var row) || row.Revision != entry.Revision) cache[(entry, started)] = row = new ProcessEventRow(entry, started);
                if (query.Length == 0 || row.Matches(query)) rows.Add(row);
            }
        }
        // Equal times: the end sorts above its own start.
        rows.Sort((left, right) => right.When != left.When ? right.When.CompareTo(left.When) : left.Started.CompareTo(right.Started));
        return rows;
    }
}

[WinRT.GeneratedWinRTExposedType]
public sealed partial class ProcessEventRows : ObservableCollection<ProcessEventRow>
{
    /// <summary>Replaces the contents; small differences keep the unchanged head and tail so the list does not jump.</summary>
    internal void Apply(IReadOnlyList<ProcessEventRow> values)
    {
        int head = 0;
        while (head < Count && head < values.Count && ReferenceEquals(this[head], values[head])) ++head;
        if (head == Count && head == values.Count) return;
        int tail = 0;
        while (tail < Count - head && tail < values.Count - head && ReferenceEquals(this[Count - 1 - tail], values[values.Count - 1 - tail])) ++tail;
        int remove = Count - head - tail, insert = values.Count - head - tail;
        if (remove + insert > 64) {
            Items.Clear();
            foreach (var value in values) Items.Add(value);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            return;
        }
        for (int i = 0; i < remove; ++i) RemoveAt(head);
        for (int i = 0; i < insert; ++i) Insert(head + i, values[head + i]);
    }
}
