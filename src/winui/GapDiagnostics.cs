using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

[Flags]
internal enum GapBreakReason : uint
{
    None = 0, NativeInvalid = 1, HistoryLost = 2, CaptureError = 4, SequenceDiscontinuity = 8,
    HistoryReadError = 16, ElapsedGap = 32, NonIncreasingTimestamp = 64, InvalidValue = 128,
    DuplicateSequence = 256
}

internal sealed class GapDiagnosticRecorder
{
    internal const int Capacity = 32768;
    internal const long DurationNs = 300_000_000_000;
    private enum EventKind { Tick, Batch, Point, Break, SequenceGap, HistoryReadError, TickError, SourceError }
    private enum HistoryRead { NotRead, NoData, Batch, Error }

    private struct Entry
    {
        internal EventKind Kind;
        internal long Now, Time, PriorTime, ProcessTime, ResourceTime;
        internal ulong Sequence, PreviousSequence, Lost, Errors;
        internal uint Series, Count, Definitions, MorePending, Interval, Flags;
        internal int View, CpuMode, WindowSeconds;
        internal SampleState NativeState, State;
        internal MetricSource Source;
        internal double Value, CollectionMs;
        internal GapBreakReason Reasons;
        internal bool Accepted;
        internal HistoryRead Read;
        internal string? Text;
    }

    private sealed class SeriesInfo(uint id, HistoryChannel channel, string resource, uint group, uint index, string key)
    {
        internal readonly uint Id = id, Group = group, Index = index;
        internal readonly HistoryChannel Channel = channel;
        internal readonly string Resource = resource, Key = key;
        internal GapBreakReason Pending;
        internal ulong Observed, Accepted, NativeInvalid, InvalidValues;
    }

    private readonly Entry[] entries = new Entry[Capacity];
    private readonly SeriesInfo?[] series = new SeriesInfo?[513];
    private readonly string path;
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private readonly long startedAt;
    private int first, count;
    private ulong overwritten, tickCount, batchCount, nativePoints, lostPoints, captureErrors, sequenceGaps, readErrors;
    private long batchTime;
    private string? processError, resourceError;
    private HistoryRead historyRead;
    private bool finished;

    internal GapDiagnosticRecorder(string path, long now) { this.path = path; startedAt = now; }

    internal void Define(uint id, HistoryChannel channel, string resource, uint group, uint index)
    {
        if (finished || id >= series.Length || series[id] is not null) return;
        string? key = (resource, channel) switch {
            ("cpu", HistoryChannel.Primary) => "cpu:total",
            ("cpu", HistoryChannel.Processor) => $"cpu:logical:{group}:{index}",
            ("memory", HistoryChannel.Primary) => "memory:used",
            ("memory", HistoryChannel.PageFile) => "memory:swap",
            _ => null
        };
        if (key is not null) series[id] = new(id, channel, resource, group, index, key);
    }

    private void Add(Entry entry)
    {
        if (finished) return;
        if (count == Capacity) { first = (first + 1) % Capacity; --count; ++overwritten; }
        entries[(first + count) % Capacity] = entry; ++count;
    }

    internal void HistoryNoData() { if (!finished) historyRead = HistoryRead.NoData; }

    internal unsafe void Batch(HistoryView view)
    {
        if (finished) return;
        historyRead = HistoryRead.Batch; batchTime = NativeMonitor.MonotonicNowNs;
        ++batchCount; nativePoints += view.PointCount; lostPoints += view.LostPoints; captureErrors += view.CaptureErrors;
        Add(new() { Kind = EventKind.Batch, Now = batchTime, Count = view.PointCount, Definitions = view.DefinitionCount,
            Lost = view.LostPoints, Errors = view.CaptureErrors, MorePending = view.MorePending });
    }

    internal void Break(GapBreakReason reason)
    {
        if (finished) return;
        foreach (var item in series) if (item is not null) item.Pending |= reason;
        Add(new() { Kind = EventKind.Break, Now = NativeMonitor.MonotonicNowNs, Reasons = reason });
    }

    internal void SequenceGap(ulong previous, ulong next)
    {
        if (finished) return;
        ++sequenceGaps;
        Add(new() { Kind = EventKind.SequenceGap, Now = batchTime, PreviousSequence = previous, Sequence = next,
            Reasons = GapBreakReason.SequenceDiscontinuity });
    }

    internal void Point(CollectedHistoryPoint point, Metric effective, long previousTime, bool accepted,
        GapBreakReason reason = GapBreakReason.None)
    {
        if (finished || point.Series >= series.Length || series[point.Series] is not { } item) return;
        ++item.Observed;
        if (accepted) ++item.Accepted;
        if (point.Metric.State != SampleState.Valid) { reason |= GapBreakReason.NativeInvalid; ++item.NativeInvalid; }
        if (point.Metric.State == SampleState.Valid && effective.State != SampleState.Valid) {
            reason |= GapBreakReason.InvalidValue; ++item.InvalidValues;
        }
        if (accepted) {
            reason |= item.Pending; item.Pending = GapBreakReason.None;
            if (previousTime > 0 && point.TimeNs - previousTime > 2_500_000_000) reason |= GapBreakReason.ElapsedGap;
        } else if (point.TimeNs <= previousTime) reason |= GapBreakReason.NonIncreasingTimestamp;
        Add(new() { Kind = EventKind.Point, Now = batchTime, Series = point.Series, Sequence = point.Sequence,
            Time = point.TimeNs, PriorTime = previousTime, NativeState = point.Metric.State, State = effective.State,
            Source = point.Metric.Source, Value = point.Metric.Value, Accepted = accepted, Reasons = reason });
    }

    internal void HistoryReadError(Exception error)
    {
        if (finished) return;
        historyRead = HistoryRead.Error; ++readErrors;
        Add(new() { Kind = EventKind.HistoryReadError, Now = NativeMonitor.MonotonicNowNs,
            Text = error.ToString(), Reasons = GapBreakReason.HistoryReadError });
    }

    internal void TickError(Exception error)
    {
        if (!finished) Add(new() { Kind = EventKind.TickError, Now = NativeMonitor.MonotonicNowNs, Text = error.ToString() });
    }

    internal bool Tick(MonitorModel model, int view, int cpuMode, int windowSeconds, string? selected, bool minimized)
    {
        if (finished) return false;
        long now = NativeMonitor.MonotonicNowNs;
        if (processError != model.ProcessError) {
            processError = model.ProcessError;
            Add(new() { Kind = EventKind.SourceError, Now = now, Text = processError, Series = 0 });
        }
        if (resourceError != model.ResourceError) {
            resourceError = model.ResourceError;
            Add(new() { Kind = EventKind.SourceError, Now = now, Text = resourceError, Series = 1 });
        }
        Add(new() { Kind = EventKind.Tick, Now = now, Sequence = ++tickCount, ProcessTime = model.ProcessTime,
            ResourceTime = model.ResourceTime, Interval = model.Interval, CollectionMs = model.CollectionMs,
            Flags = (model.Paused ? 1u : 0) | (model.Stale ? 2u : 0) | (minimized ? 4u : 0),
            View = view, CpuMode = cpuMode, WindowSeconds = windowSeconds, Text = selected, Read = historyRead });
        historyRead = HistoryRead.NotRead;
        return now - startedAt >= DurationNs;
    }

    internal void Finish(string reason, BootIdentityInfo boot)
    {
        if (finished) return;
        long endedAt = NativeMonitor.MonotonicNowNs;
        finished = true;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1); writer.WriteString("kind", "actual-gui-history-gaps");
        writer.WriteString("startedUtc", startedUtc); writer.WriteString("endedUtc", DateTimeOffset.UtcNow);
        writer.WriteNumber("startedAtNs", startedAt); writer.WriteNumber("endedAtNs", endedAt);
        writer.WriteNumber("requestedDurationNs", DurationNs); writer.WriteString("finishReason", reason);
        writer.WriteBoolean("durationCompleted", reason == "durationElapsed" && endedAt - startedAt >= DurationNs);
        writer.WriteBoolean("diagnosticRingComplete", overwritten == 0);
        writer.WriteNumber("capacity", Capacity); writer.WriteNumber("retainedEvents", count);
        writer.WriteNumber("overwrittenDiagnosticEvents", overwritten);
        writer.WriteString("clock", "nks_monitor_monotonic_now_ns; history TimeNs is native steady_clock");
        writer.WriteString("sequenceScope", "Global native sequence includes unrecorded non-CPU/memory series; use sequenceGap events, not adjacent filtered points.");
        writer.WriteString("tickTimeMeaning", "Completion of MainWindow Tick finally; not model execution duration.");
        writer.WriteString("breakMeaning", "Native state, managed continuity flags and renderer elapsed threshold are separate; values are unchanged.");
        writer.WriteNumber("rendererElapsedGapThresholdNs", 2_500_000_000);
        writer.WriteStartObject("sourceIdentity");
        var assembly = typeof(App).Assembly;
        writer.WriteString("assemblyVersion", assembly.GetName().Version?.ToString());
        writer.WriteString("sourceVersion", assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        writer.WriteString("appContextSourceVersion", AppContext.GetData("SourceVersion") as string);
        writer.WriteString("baseDirectory", AppContext.BaseDirectory);
        WriteImage(writer, "gui", Environment.ProcessPath);
        WriteImage(writer, "nativeMonitor", Path.Combine(AppContext.BaseDirectory, "nks-monitor.dll"));
        WriteImage(writer, "collectorWorker", Path.Combine(AppContext.BaseDirectory, "nks-collector-worker.exe"));
        writer.WriteEndObject();
        writer.WriteStartObject("bootAtFinish"); writer.WriteNumber("low", boot.Low); writer.WriteNumber("high", boot.High);
        writer.WriteNumber("state", (uint)boot.State); writer.WriteEndObject();
        writer.WriteStartObject("observedTotals");
        writer.WriteNumber("ticks", tickCount); writer.WriteNumber("batches", batchCount); writer.WriteNumber("allNativePoints", nativePoints);
        writer.WriteNumber("nativeLostPoints", lostPoints); writer.WriteNumber("nativeCaptureErrors", captureErrors);
        writer.WriteNumber("globalSequenceGaps", sequenceGaps); writer.WriteNumber("historyReadErrors", readErrors);
        writer.WriteEndObject();
        WriteLegend(writer);
        var retained = new ulong[series.Length];
        for (int i = 0; i < count; ++i) {
            ref var entry = ref entries[(first + i) % Capacity];
            if (entry.Kind == EventKind.Point) ++retained[entry.Series];
        }
        writer.WriteStartArray("channelWeights");
        foreach (var item in series) {
            if (item is null) continue;
            writer.WriteStartObject(); writer.WriteNumber("series", item.Id); writer.WriteString("seriesKey", item.Key);
            writer.WriteString("resource", item.Resource); writer.WriteNumber("channel", (uint)item.Channel);
            writer.WriteString("channelName", item.Channel.ToString()); writer.WriteNumber("group", item.Group); writer.WriteNumber("index", item.Index);
            writer.WriteNumber("observedPoints", item.Observed); writer.WriteNumber("retainedPoints", retained[item.Id]);
            writer.WriteNumber("acceptedPoints", item.Accepted); writer.WriteNumber("nativeInvalidPoints", item.NativeInvalid);
            writer.WriteNumber("invalidValuePoints", item.InvalidValues); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("events");
        for (int i = 0; i < count; ++i) WriteEntry(writer, entries[(first + i) % Capacity]);
        writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush(); stream.Flush(true);
    }

    private static void WriteImage(Utf8JsonWriter writer, string name, string? image)
    {
        writer.WriteStartObject(name); writer.WriteString("path", image);
        if (image is not null) {
            try {
                using var file = File.OpenRead(image);
                writer.WriteString("sha256AtFlush", Convert.ToHexString(SHA256.HashData(file)));
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
                writer.WriteString("hashError", error.ToString());
            }
        }
        writer.WriteEndObject();
    }

    private static void WriteLegend(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("sampleStates");
        foreach (var state in Enum.GetValues<SampleState>()) writer.WriteNumber(state.ToString(), (uint)state);
        writer.WriteEndObject(); writer.WriteStartObject("metricSources");
        foreach (var source in Enum.GetValues<MetricSource>()) writer.WriteNumber(source.ToString(), (uint)source);
        writer.WriteEndObject(); writer.WriteStartObject("breakReasons");
        foreach (var reason in Enum.GetValues<GapBreakReason>()) writer.WriteNumber(reason.ToString(), (uint)reason);
        writer.WriteEndObject(); writer.WriteStartObject("tickFlags");
        writer.WriteNumber("paused", 1); writer.WriteNumber("stale", 2); writer.WriteNumber("minimized", 4); writer.WriteEndObject();
    }

    private void WriteEntry(Utf8JsonWriter writer, Entry entry)
    {
        writer.WriteStartObject(); writer.WriteString("kind", entry.Kind.ToString()); writer.WriteNumber("observedAtNs", entry.Now);
        switch (entry.Kind) {
            case EventKind.Tick:
                writer.WriteNumber("tick", entry.Sequence); writer.WriteNumber("processTimeNs", entry.ProcessTime);
                writer.WriteNumber("resourceTimeNs", entry.ResourceTime); writer.WriteNumber("intervalMs", entry.Interval);
                WriteNumber(writer, "collectionMs", entry.CollectionMs); writer.WriteNumber("flags", entry.Flags);
                writer.WriteNumber("view", entry.View); writer.WriteNumber("cpuMode", entry.CpuMode);
                writer.WriteNumber("windowSeconds", entry.WindowSeconds); writer.WriteString("selectedResource", entry.Text);
                writer.WriteString("historyRead", entry.Read.ToString()); break;
            case EventKind.Batch:
                writer.WriteNumber("pointCount", entry.Count); writer.WriteNumber("definitionCount", entry.Definitions);
                writer.WriteNumber("lostPoints", entry.Lost); writer.WriteNumber("captureErrors", entry.Errors);
                writer.WriteNumber("morePending", entry.MorePending); break;
            case EventKind.Point:
                writer.WriteNumber("series", entry.Series); writer.WriteString("seriesKey", series[entry.Series]!.Key);
                writer.WriteNumber("globalSequence", entry.Sequence); writer.WriteNumber("timeNs", entry.Time);
                writer.WriteNumber("previousAcceptedTimeNs", entry.PriorTime);
                writer.WriteNumber("nativeState", (uint)entry.NativeState); writer.WriteString("nativeStateName", entry.NativeState.ToString());
                writer.WriteNumber("effectiveState", (uint)entry.State); writer.WriteString("effectiveStateName", entry.State.ToString());
                writer.WriteNumber("source", (uint)entry.Source); writer.WriteBoolean("accepted", entry.Accepted);
                WriteNumber(writer, "nativeValue", entry.Value);
                WriteReasons(writer, entry.Reasons); break;
            case EventKind.SequenceGap:
                writer.WriteNumber("previousGlobalSequence", entry.PreviousSequence); writer.WriteNumber("nextGlobalSequence", entry.Sequence);
                WriteReasons(writer, entry.Reasons); break;
            case EventKind.Break: WriteReasons(writer, entry.Reasons); break;
            case EventKind.SourceError:
                writer.WriteString("source", entry.Series == 0 ? "process" : "resource"); writer.WriteString("error", entry.Text); break;
            default: writer.WriteString("error", entry.Text); WriteReasons(writer, entry.Reasons); break;
        }
        writer.WriteEndObject();
    }

    private static void WriteReasons(Utf8JsonWriter writer, GapBreakReason reasons)
    {
        writer.WriteNumber("breakReasons", (uint)reasons); writer.WriteString("breakReasonNames", reasons.ToString());
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, double value)
    {
        if (double.IsFinite(value)) writer.WriteNumber(name, value);
        else writer.WriteString(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

public sealed partial class MonitorModel
{
    private GapDiagnosticRecorder? gapDiagnostics;

    internal void BeginGapDiagnostics(string path)
    {
        if (gapDiagnostics is not null) throw new InvalidOperationException("Gap diagnostics are already configured.");
        gapDiagnostics = new(path, NativeMonitor.MonotonicNowNs);
        foreach (var pair in collectedSeries) {
            var series = pair.Value;
            gapDiagnostics.Define(pair.Key, series.Channel, series.Resource, series.Group, series.Index);
        }
    }

    internal bool RecordGapDiagnosticTick(int view, int cpuMode, int windowSeconds, string? selected, bool minimized) =>
        gapDiagnostics?.Tick(this, view, cpuMode, windowSeconds, selected, minimized) == true;

    internal void RecordGapDiagnosticError(Exception error) => gapDiagnostics?.TickError(error);
    internal void FinishGapDiagnostics(string reason) => gapDiagnostics?.Finish(reason, BootIdentity);
}
