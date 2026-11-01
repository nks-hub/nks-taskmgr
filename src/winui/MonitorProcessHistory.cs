using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MonitorModel
{
    private readonly ProcessHistoryStore processHistory = new();
    internal void CaptureProcessHistory() => processHistory.Capture(ProcessTime, ProcessWallTimeUnixNs, processRevision, processes.Values,
        measurementsAvailable ? processMeasurements.ProcessCpu : null, measurementsAvailable ? processMeasurements.ProcessIo : null);
    internal ProcessHistorySelection SelectProcessHistory(long startNs, long endNs) => processHistory.Select(startNs, endNs);
}

internal sealed class ProcessHistoryStore
{
    private const long MaximumAgeNs = 600_000_000_000, MaximumBytes = 4 * 1024 * 1024 - 196608, RowBytes = 768, FrameBytes = 256;
    private const int MaximumRows = 8192;
    private sealed record Frame(long Time, long WallTime, ulong Revision, int OriginalCount, ProcessHistorySample[] Rows, bool Gap, ulong? MissingFrames);
    private readonly LinkedList<Frame> frames = new();
    private long lastTime, bytes;
    private ulong lastRevision, evictedFrames, evictedRows;
    private int rowCount;

    internal void Capture(long time, long wallTime, ulong revision, IReadOnlyCollection<ProcessRow> source, MeasurementMetadata? cpu, MeasurementMetadata? io)
    {
        if (time <= 0 || time <= lastTime) return;
        ulong? missing = lastRevision != 0 && revision > lastRevision ? revision - lastRevision - 1 : null;
        bool gap = lastTime != 0 && (time - lastTime > MonitorModel.StaleAfterNs || missing > 0 || revision != 0 && lastRevision != 0 && revision <= lastRevision);
        bool late = NativeMonitor.MonotonicNowNs - time > MonitorModel.StaleAfterNs;
        var chosen = ProcessHistoryRanking.Pick(source);
        var rows = new ProcessHistorySample[chosen.Length];
        for (int i = 0; i < chosen.Length; ++i) {
            var row = chosen[i]; int length = Math.Min(row.Name.Length, 96);
            if (length > 0 && length < row.Name.Length && char.IsHighSurrogate(row.Name[length - 1])) --length;
            string name = row.Name[..length].Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            rows[i] = new(row.Key, row.Created100ns, time, wallTime, name, row.Name.Length, row.Cpu, row.WorkingBytes, row.PrivateBytes,
                row.Stale ? SampleState.Stale : SampleState.Valid, row.ReadIo, row.WriteIo, late || row.Stale, cpu, io);
        }
        frames.AddLast(new Frame(time, wallTime, revision, source.Count, rows, gap, missing));
        bytes += FrameBytes + RowBytes * rows.Length; rowCount += rows.Length; lastTime = time; lastRevision = revision;
        while (frames.First is { } first && (bytes > MaximumBytes || rowCount > MaximumRows || time - first.Value.Time > MaximumAgeNs)) {
            bytes -= FrameBytes + RowBytes * first.Value.Rows.Length; rowCount -= first.Value.Rows.Length;
            ++evictedFrames; evictedRows += (ulong)first.Value.Rows.Length; frames.RemoveFirst();
        }
    }

    internal ProcessHistorySelection Select(long start, long end)
    {
        if (end < start) (start, end) = (end, start);
        bool point = start == end; long first = frames.First?.Value.Time ?? 0, last = frames.Last?.Value.Time ?? 0;
        Frame? nearest = null;
        long firstSpacing = frames.First?.Next is { } next ? next.Value.Time - first : 0;
        long lastSpacing = frames.Last?.Previous is { } before ? last - before.Value.Time : 0;
        bool nearBounds = start > 0 && first > 0 && (start >= first && start <= last
            || start < first && first - start <= Math.Min(firstSpacing, MonitorModel.StaleAfterNs)
            || start > last && start - last <= Math.Min(lastSpacing, MonitorModel.StaleAfterNs));
        if (point && nearBounds) {
            foreach (var frame in frames) if (nearest is null || Math.Abs(frame.Time - start) < Math.Abs(nearest.Time - start)) nearest = frame;
        }
        var cpu = new List<ProcessHistorySample>(64); var memory = new List<ProcessHistorySample>(64); var io = new List<ProcessHistorySample>(64);
        var gaps = new List<ProcessHistoryGap>(32); int count = 0, gapCount = 0, minOriginal = int.MaxValue, maxOriginal = 0;
        long original = 0, stored = 0, firstSelected = 0, lastSelected = 0, firstWall = 0, lastWall = 0;
        ulong missing = 0; bool unknown = false; Frame? previous = null;
        foreach (var frame in frames) {
            bool selected = point ? ReferenceEquals(frame, nearest) : frame.Time >= start && frame.Time <= end;
            bool cursorInGap = point && frame.Gap && previous is not null && start > previous.Time && start < frame.Time;
            if ((selected || cursorInGap) && frame.Gap && previous is not null) {
                ++gapCount; if (gaps.Count < 32) gaps.Add(new(previous.Time, frame.Time, frame.MissingFrames));
            }
            if (cursorInGap && !selected) missing += frame.MissingFrames ?? 0;
            if (selected) {
                if (count++ == 0) { firstSelected = frame.Time; firstWall = frame.WallTime; }
                lastSelected = frame.Time; lastWall = frame.WallTime; original += frame.OriginalCount; stored += frame.Rows.Length;
                minOriginal = Math.Min(minOriginal, frame.OriginalCount); maxOriginal = Math.Max(maxOriginal, frame.OriginalCount);
                unknown |= frame.Revision == 0 || frame.MissingFrames is null;
                missing += frame.MissingFrames ?? 0;
                foreach (var row in frame.Rows) {
                    if (point) { cpu.Add(row); memory.Add(row); io.Add(row); }
                    else { ProcessHistoryRanking.Keep(cpu, row, ProcessHistoryMetric.Cpu); ProcessHistoryRanking.Keep(memory, row, ProcessHistoryMetric.Memory); ProcessHistoryRanking.Keep(io, row, ProcessHistoryMetric.Io); }
                }
            }
            previous = frame;
        }
        if (point) {
            cpu.Sort((a, b) => ProcessHistoryRanking.Score(b, ProcessHistoryMetric.Cpu).CompareTo(ProcessHistoryRanking.Score(a, ProcessHistoryMetric.Cpu)));
            memory.Sort((a, b) => b.WorkingBytes.CompareTo(a.WorkingBytes));
            io.Sort((a, b) => ProcessHistoryRanking.Score(b, ProcessHistoryMetric.Io).CompareTo(ProcessHistoryRanking.Score(a, ProcessHistoryMetric.Io)));
        }
        string notice = count == 0 ? point ? "U zvoleného času není dostupný uchovaný procesový snímek." : "Ve zvoleném intervalu není uchovaný procesový snímek."
            : point ? "Nejbližší skutečně uchovaný procesový snímek; hodnoty nebyly dopočítané ke kurzoru."
            : "Maximum každého řádku pochází z jediného skutečně uchovaného vzorku. Přehled obsahuje jen uchovaná pozorování vybraných procesů; nezachycené hodnoty nejsou známé.";
        return new(start, end, first, last, firstSelected, lastSelected, firstWall, lastWall, count, original, stored,
            count == 0 ? 0 : minOriginal, maxOriginal, gapCount, missing, unknown, frames.Count, rowCount, bytes + 196608,
            evictedFrames, evictedRows, nearest is null ? 0 : nearest.Time - start, gaps.AsReadOnly(), cpu.AsReadOnly(), memory.AsReadOnly(), io.AsReadOnly(), notice);
    }
}
