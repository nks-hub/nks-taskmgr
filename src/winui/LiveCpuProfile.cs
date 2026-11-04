using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly TickCpuProfile? liveCpuProfile = TickCpuProfile.FromArguments();
    private long BeginLiveCpuTick() => liveCpuProfile?.Begin(view) ?? 0;
    private void SetLiveCpuState(long token, bool minimized) => liveCpuProfile?.SetState(token, minimized);
    private void MarkLiveCpuModel(long token, bool start) => liveCpuProfile?.MarkModel(token, start);
    private void EndLiveCpuTick(long token) => liveCpuProfile?.End(token);
    private Task<bool> FlushLiveCpuProfileAsync() => liveCpuProfile?.CloseAsync() ?? Task.FromResult(true);

    private sealed class TickCpuProfile
    {
        private const int MaximumSegments = 64;
        private readonly record struct Sample(int Number, DesktopView View, bool? Minimized, long StartQpc, long EndQpc,
            long Ticks, long ModelTicks, ulong? UiCpu, ulong? TickCpu, ulong? ModelCpu, long Errors, long Reentrant, string Reason);
        private readonly Channel<Sample> queue = Channel.CreateBounded<Sample>(new BoundedChannelOptions(8) {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        private readonly Task writer;
        private bool closed, limited, segment, known, tickKnown, modelKnown, startKnown, endKnown, modelStartKnown, modelEndKnown;
        private bool modelStarted, modelEnded, ioFailed;
        private bool? minimized, tickMinimized;
        private DesktopView segmentView, tickView;
        private long serial, active, tickStartQpc, firstQpc, lastQpc, count, modelCount, errors, tickErrors, segmentErrors, lastErrors;
        private long reentrant, tickReentrant, segmentReentrant, lastReentrant, dropped, incompleteSegments;
        private ulong tickStartCpu, tickEndCpu, firstCpu, lastCpu, modelStartCpu, modelEndCpu, ticksCpu, modelsCpu;
        private int segments, persisted, lastIoError;
        internal int PersistenceError => Volatile.Read(ref lastIoError);

        internal static TickCpuProfile? FromArguments()
        {
            const string prefix = "--profile-ticks=";
            foreach (var arg in LaunchArguments.Current)
                if (arg.StartsWith(prefix, StringComparison.Ordinal)) return new TickCpuProfile(arg[prefix.Length..]);
            return null;
        }
        private TickCpuProfile(string path) => writer = Task.Run(() => PersistAsync(path));

        private bool ReadCpu(out ulong cpu)
        {
            try { cpu = UiThreadCpu100Ns(); return true; }
            catch (System.ComponentModel.Win32Exception) { ++errors; cpu = 0; return false; }
        }
        internal long Begin(DesktopView currentView)
        {
            if (closed || limited) return 0;
            if (active != 0) { ++reentrant; known = tickKnown = modelKnown = false; return 0; }
            active = ++serial; tickView = currentView; tickMinimized = null;
            tickErrors = errors; tickReentrant = reentrant;
            modelStarted = modelEnded = false;
            tickStartQpc = Stopwatch.GetTimestamp(); startKnown = ReadCpu(out tickStartCpu);
            return active;
        }
        internal void SetState(long token, bool value)
        {
            if (token == 0 || active != token || closed) return;
            tickMinimized = value;
        }
        internal void MarkModel(long token, bool start)
        {
            if (token == 0 || active != token || closed) return;
            if (start) { modelStarted = true; modelStartKnown = ReadCpu(out modelStartCpu); }
            else { modelEnded = true; modelEndKnown = ReadCpu(out modelEndCpu); }
        }
        internal void End(long token)
        {
            if (token == 0 || active != token) return;
            endKnown = ReadCpu(out tickEndCpu); long endQpc = Stopwatch.GetTimestamp(); active = 0;
            if (closed) return;
            if (segment && (segmentView != tickView || minimized != tickMinimized)) Seal("state-change");
            if (limited) return;
            if (!segment) {
                segment = true; segmentView = tickView; minimized = tickMinimized;
                firstQpc = tickStartQpc; firstCpu = tickStartCpu; known = tickKnown = modelKnown = startKnown;
                count = modelCount = 0; ticksCpu = modelsCpu = 0;
                segmentErrors = tickErrors; segmentReentrant = tickReentrant;
            }
            ++count; lastQpc = endQpc; lastCpu = tickEndCpu;
            known &= startKnown && endKnown && tickEndCpu >= firstCpu;
            if (startKnown && endKnown && tickEndCpu >= tickStartCpu) ticksCpu += tickEndCpu - tickStartCpu;
            else { tickKnown = false; if (startKnown && endKnown) ++errors; }
            if (modelStarted && modelEnded && modelStartKnown && modelEndKnown && modelEndCpu >= modelStartCpu) {
                ++modelCount; modelsCpu += modelEndCpu - modelStartCpu;
            } else modelKnown = false;
            if (modelStarted && modelEnded && modelStartKnown && modelEndKnown &&
                (modelEndCpu < modelStartCpu || startKnown && modelStartCpu < tickStartCpu || endKnown && modelEndCpu > tickEndCpu)) ++errors;
            lastErrors = errors; lastReentrant = reentrant;
            if (endQpc - firstQpc >= 60L * Stopwatch.Frequency) Seal("window");
        }
        private void Seal(string reason)
        {
            if (!segment) return;
            bool clean = lastErrors == segmentErrors && lastReentrant == segmentReentrant;
            var sample = new Sample(++segments, segmentView, minimized, firstQpc, lastQpc, count, modelCount,
                known && clean ? lastCpu - firstCpu : null, tickKnown && clean ? ticksCpu : null,
                modelKnown && clean && modelCount == count ? modelsCpu : null,
                lastErrors - segmentErrors, lastReentrant - segmentReentrant, reason);
            if (sample.UiCpu is null || sample.TickCpu is null || sample.ModelCpu is null || sample.Minimized is null
                || sample.UiCpu is { } ui && sample.TickCpu is { } tick && tick > ui
                || sample.TickCpu is { } outer && sample.ModelCpu is { } model && model > outer) ++incompleteSegments;
            if (!queue.Writer.TryWrite(sample)) ++dropped;
            segment = false;
            if (segments >= MaximumSegments) limited = true;
        }
        internal async Task<bool> CloseAsync()
        {
            if (!closed) {
                if (active != 0) { ++reentrant; known = tickKnown = modelKnown = false; }
                Seal("close"); closed = true; queue.Writer.TryComplete();
            }
            try { await writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { return false; }
            return !Volatile.Read(ref ioFailed) && !limited && segments > 0 && incompleteSegments == 0 && dropped == 0 && errors == 0 && reentrant == 0;
        }
        private async Task PersistAsync(string path)
        {
            try {
                if (!Path.IsPathFullyQualified(path)) throw new IOException("The diagnostic path must be absolute.");
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
                await foreach (var sample in queue.Reader.ReadAllAsync().ConfigureAwait(false)) {
                    var buffer = new ArrayBufferWriter<byte>();
                    using (var json = new Utf8JsonWriter(buffer)) WriteSample(json, sample);
                    await file.WriteAsync(buffer.WrittenMemory).ConfigureAwait(false);
                    await file.WriteAsync(new byte[] { 10 }).ConfigureAwait(false);
                    await file.FlushAsync().ConfigureAwait(false); file.Flush(true); ++persisted;
                }
                var final = new ArrayBufferWriter<byte>();
                using (var json = new Utf8JsonWriter(final)) {
                    json.WriteStartObject(); json.WriteString("kind", "completion"); json.WriteBoolean("diagnosticOnly", true);
                    json.WriteNumber("queuedSegments", segments - dropped); json.WriteNumber("persistedSegments", persisted);
                    json.WriteNumber("droppedSegments", dropped); json.WriteNumber("counterErrors", errors);
                    json.WriteNumber("incompleteSegments", incompleteSegments);
                    json.WriteNumber("reentrantTicks", reentrant); json.WriteBoolean("limitReached", limited);
                    json.WriteBoolean("complete", !limited && persisted > 0 && incompleteSegments == 0 && dropped == 0 && errors == 0 && reentrant == 0);
                    json.WriteEndObject();
                }
                await file.WriteAsync(final.WrittenMemory).ConfigureAwait(false);
                await file.WriteAsync(new byte[] { 10 }).ConfigureAwait(false);
                await file.FlushAsync().ConfigureAwait(false); file.Flush(true);
            } catch (Exception error) {
                Interlocked.Exchange(ref lastIoError, error.HResult); Volatile.Write(ref ioFailed, true);
            }
        }
        private static void Cpu(Utf8JsonWriter json, string name, ulong? value)
        {
            if (value is { } cpu) json.WriteNumber(name, cpu); else json.WriteNull(name);
        }
        private static void WriteSample(Utf8JsonWriter json, Sample sample)
        {
            json.WriteStartObject(); json.WriteString("kind", "segment"); json.WriteBoolean("diagnosticOnly", true);
            json.WriteBoolean("replacesJobAccounting", false); json.WriteBoolean("cpuUnitsImplyResolution", false);
            json.WriteBoolean("phaseCpuAttributionReliable", false);
            json.WriteString("phaseCpuCaveat", "Thread CPU counters can be coarse or deferred. Short-phase deltas do not reliably attribute CPU work.");
            json.WriteBoolean("stateAtTickEntryOnly", true); json.WriteBoolean("continuousStateProof", false);
            json.WriteString("view", sample.View.ToString());
            if (sample.Minimized is { } minimized) json.WriteBoolean("minimized", minimized); else json.WriteNull("minimized");
            json.WriteNumber("number", sample.Number); json.WriteString("endReason", sample.Reason);
            json.WriteNumber("startQpc", sample.StartQpc); json.WriteNumber("endQpc", sample.EndQpc);
            json.WriteNumber("qpcFrequency", Stopwatch.Frequency);
            json.WriteNumber("seconds", (sample.EndQpc - sample.StartQpc) / (double)Stopwatch.Frequency);
            json.WriteNumber("ticks", sample.Ticks); json.WriteNumber("modelMeasuredTicks", sample.ModelTicks);
            Cpu(json, "uiThreadCpu100Ns", sample.UiCpu); Cpu(json, "tickCounterDelta100Ns", sample.TickCpu); Cpu(json, "modelCounterDelta100Ns", sample.ModelCpu);
            Cpu(json, "otherTickCounterRemainder100Ns", sample.TickCpu is { } tick && sample.ModelCpu is { } model && tick >= model ? tick - model : null);
            Cpu(json, "cpuCounterRemainder100Ns", sample.UiCpu is { } ui && sample.TickCpu is { } ticks && ui >= ticks ? ui - ticks : null);
            json.WriteNumber("counterErrors", sample.Errors); json.WriteNumber("reentrantTicks", sample.Reentrant);
            json.WriteString("cpuCounterRemainderMeaning", "Coarse counter remainder; not proof of work outside Tick or in XAML. Includes profiling overhead.");
            json.WriteEndObject();
        }
    }
}
