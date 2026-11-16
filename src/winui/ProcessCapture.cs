using System.Diagnostics;
using Microsoft.UI.Dispatching;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

/// <summary>
/// Owns the application's ETW capture for its whole life: reads the native monitor every 250 ms, feeds start and stop events to the
/// timeline and traffic windows to <see cref="ProcessNetwork"/>. The capture needs an elevated token and is never started otherwise.
/// </summary>
internal sealed class ProcessCapture : IDisposable
{
    private readonly DispatcherQueueTimer timer;
    private readonly ProcessTimeline timeline;
    private readonly Func<(long Time, BootIdentifier Boot, SampleState State)> currentProcesses;
    private NativeEventMonitor? monitor;
    private ulong resyncVersion;
    private long resyncAfter;
    private bool trafficSupported = true, disposed, failed;
    private Task<bool>? closeStop;

    internal ProcessCapture(DispatcherQueue queue, ProcessTimeline timeline, Func<(long Time, BootIdentifier Boot, SampleState State)> currentProcesses)
    {
        this.timeline = timeline; this.currentProcesses = currentProcesses;
        timer = queue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(250); timer.Tick += (_, _) => Read();
    }

    /// <summary>Metadata of the last read; null until the capture started.</summary>
    internal ProcessEventBatchInfo? Info { get; private set; }
    /// <summary>Why the capture could not start or read; empty while it works.</summary>
    internal string Error { get; private set; } = "";
    /// <summary>A session was started and has not failed; only then does closing need to stop and drain it.</summary>
    internal bool Active => monitor is not null && !disposed && !failed;
    internal ProcessEventMonitorState State => Info?.State ?? ProcessEventMonitorState.Idle;
    internal bool NeedsCleanup => monitor is not null && (Info is { } info && (info.Flags & ProcessEventFrameFlags.CleanupOutstanding) != 0
        || State is ProcessEventMonitorState.Starting or ProcessEventMonitorState.Running or ProcessEventMonitorState.Stopping);

    /// <summary>Starts the session and the read timer; false with <see cref="Error"/> set when the native monitor is unusable.</summary>
    internal bool Start()
    {
        if (disposed) return false;
        try {
            monitor ??= NativeEventMonitor.Open();
            resyncVersion = 0; resyncAfter = 0; failed = false;
            monitor.Start(); timer.Start();
            return Read() >= 0;
        } catch (Exception error) when (InteropError(error)) {
            Error = Loc.F("Záznam nelze spustit: {0}", error.Message); return false;
        }
    }

    /// <summary>Reads the monitor once; returns the number of records read or -1 when reading failed.</summary>
    private int Read()
    {
        if (disposed || monitor is null) return 0;
        try {
            using var frame = monitor.Read();
            var info = frame.Info;
            Info = info; Error = "";
            CapabilityReports.ObserveEvents(info);
            var events = frame.CopyEvents();
            // Event times are QPC ticks; they convert to wall-clock time only on the same clock as Stopwatch.
            bool sameClock = info.QpcFrequency == Stopwatch.Frequency;
            if (events.Length > 0) timeline.ObserveEvents(events, NativeMonitor.MonotonicNowNs, sameClock ? Stopwatch.GetTimestamp() : 0, sameClock ? info.QpcFrequency : 0);
            int traffic = ReadTraffic(frame, info);
            if ((info.Flags & ProcessEventFrameFlags.ResyncRequired) != 0) {
                if (resyncVersion != info.ResyncVersion) { resyncVersion = info.ResyncVersion; resyncAfter = NativeMonitor.MonotonicNowNs; }
                var processes = currentProcesses();
                if (info.BootState == SampleState.Valid && processes.State == SampleState.Valid && processes.Boot == info.Boot && processes.Time > resyncAfter)
                    monitor.AcknowledgeResync(info.ResyncVersion);
            }
            return events.Length + traffic;
        } catch (Exception error) when (InteropError(error)) {
            timer.Stop(); ProcessNetwork.Clear(); failed = true;
            // A broken reader cannot drain at close; stopping here lets the next start clean a session Windows still holds.
            try { monitor.Stop(); } catch (Exception stop) when (InteropError(stop)) { }
            Error = Loc.F("Čtení událostí selhalo: {0}", error.Message); return -1;
        }
    }

    private int ReadTraffic(NativeEventFrame frame, ProcessEventBatchInfo info)
    {
        if (!trafficSupported) { ProcessNetwork.Clear(); return 0; }
        ProcessTrafficBatch batch;
        try { batch = frame.CopyTraffic(); }
        catch (EntryPointNotFoundException) { trafficSupported = false; ProcessNetwork.Clear(); return 0; }
        catch { ProcessNetwork.Clear(); throw; }
        ProcessNetwork.Observe(info, batch);
        return batch.Samples.Length;
    }

    /// <summary>Stops the session and reads until Windows confirms cleanup and the queue is empty, for at most five seconds.</summary>
    internal Task<bool> StopForCloseAsync() => closeStop is { IsCompleted: false } pending ? pending : closeStop = DrainForCloseAsync();

    /// <summary>Starts the session again after a close was cancelled.</summary>
    internal void Resume() { if (monitor is not null && !disposed) Start(); }

    private async Task<bool> DrainForCloseAsync()
    {
        if (monitor is null) return true;
        try {
            monitor.Stop();
            long began = Stopwatch.GetTimestamp();
            do {
                int count = Read();
                if (count < 0) return false;
                // Stopped confirms session cleanup, not an empty lifecycle queue.
                if (!NeedsCleanup && count == 0) { ProcessNetwork.Clear(); return true; }
                if (NeedsCleanup) await Task.Delay(50);
                else await Task.Yield();
            } while (Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(5));
            return false;
        } catch (Exception error) when (InteropError(error)) {
            Error = Loc.F("Úklid záznamu nebyl potvrzený: {0}", error.Message); return false;
        }
    }

    public void Dispose()
    {
        disposed = true; timer.Stop(); monitor?.Dispose(); monitor = null;
        ProcessNetwork.Clear();
    }

    private static bool InteropError(Exception error) => error is IOException or InvalidDataException or NativeMonitorException or EntryPointNotFoundException or DllNotFoundException
        or BadImageFormatException or InvalidOperationException or ArgumentException;
}
