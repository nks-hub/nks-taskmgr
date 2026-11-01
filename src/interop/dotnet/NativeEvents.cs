using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NksTaskMgr.Native;

public enum ProcessEventMonitorState : uint
{
    Idle, Starting, Running, Stopping, Stopped, AccessDenied, Unsupported, Error, Busy, StopTimedOut, CleanupFailed
}
public enum ProcessEventKind : uint { Started = 1, Stopped = 2 }
[Flags]
public enum ProcessEventFlags : uint { HasExitStatus = 1, HasImageName = 2, Partial = 4, HasParentPid = 8 }
[Flags]
public enum ProcessEventFrameFlags : uint { ResyncRequired = 1, CleanupOutstanding = 2 }

public readonly record struct ProcessEventInfo(ulong Sequence, long TimestampQpc, long ObservedAtNs, ProcessEventKind Kind,
    uint Pid, uint ParentPid, SampleState IdentityState, GenerationKind IdentityKind, ulong Generation,
    ProcessEventFlags Flags, int ExitStatus, string ImageName);
public readonly record struct ProcessEventBatchInfo(ProcessEventMonitorState State, uint Win32Error, ProcessEventFrameFlags Flags,
    BootIdentifier Boot, SampleState BootState, long QpcFrequency, ulong ProviderEventsLost, ulong ProviderBuffersLost,
    ulong QueueDropped, ulong DecodeErrors, ulong Ambiguous, ulong ResyncVersion);

public sealed class NativeEventMonitor : SafeHandleZeroOrMinusOneIsInvalid
{
    private NativeEventMonitor() : base(true) { }
    public static NativeEventMonitor Open()
    {
        var monitor = new NativeEventMonitor();
        Check(EventMethods.Create(out nint handle)); monitor.SetHandle(handle); return monitor;
    }
    public void Start() => Check(EventMethods.Start(this));
    public void Stop() => EventMethods.Stop(this);
    public void AcknowledgeResync(ulong version) => EventMethods.Acknowledge(this, version);
    public NativeEventFrame Read()
    {
        Check(EventMethods.Read(this, out NativeEventFrame frame)); return frame;
    }
    public async Task<ProcessEventMonitorState> WaitForStopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        long began = Stopwatch.GetTimestamp();
        for (;;) {
            cancellationToken.ThrowIfCancellationRequested();
            using var frame = Read();
            var info = frame.Info;
            if (info.State is ProcessEventMonitorState.StopTimedOut or ProcessEventMonitorState.CleanupFailed
                || !info.Flags.HasFlag(ProcessEventFrameFlags.CleanupOutstanding)) return info.State;
            if (Stopwatch.GetElapsedTime(began) >= timeout) return ProcessEventMonitorState.StopTimedOut;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }
    private static void Check(NativeResult result)
    {
        if (result == NativeResult.NoData) throw new InvalidOperationException("Je zadrženo příliš mnoho rámců událostí.");
        if (result != NativeResult.Ok) throw new InvalidOperationException($"Čtení událostí selhalo: {result}.");
    }
    protected override bool ReleaseHandle() { EventMethods.ReleaseMonitor(handle); return true; }
}

public sealed unsafe class NativeEventFrame : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeEventFrame() : base(true) { }
    public ProcessEventBatchInfo Info => Use<ProcessEventBatchInfo>(view => new((ProcessEventMonitorState)view.State, view.Win32Error,
        (ProcessEventFrameFlags)view.Flags, new(view.BootLow, view.BootHigh), (SampleState)view.BootState, view.QpcFrequency,
        view.ProviderEventsLost, view.ProviderBuffersLost, view.QueueDropped, view.DecodeErrors, view.Ambiguous, view.ResyncVersion));

    public ProcessEventInfo[] CopyEvents() => Use(view => {
        var records = new ProcessEventInfo[view.EventCount];
        for (int i = 0; i < records.Length; ++i) {
            var row = view.Events[i];
            if (row.Sequence == 0 || row.TimestampQpc < 0 || row.ObservedAtNs < 0 || row.Kind is < 1 or > 2
                || row.IdentityState != SampleState.Unavailable || row.IdentityKind != GenerationKind.Missing
                || row.Generation != 0 || (row.Flags & ~15u) != 0 || row.Reserved != 0 || row.ImageName.Reserved != 0
                || (row.Flags & 1) == 0 && row.ExitStatus != 0 || (row.Flags & 8) == 0 && row.ParentPid != 0
                || (row.Flags & 2) == 0 && row.ImageName.Length != 0
                || row.ImageName.Length > 1024 || row.ImageName.Length > 0 && row.ImageName.Data == null)
                throw new InvalidDataException("Pracovník událostí vrátil neplatný záznam.");
            records[i] = new(row.Sequence, row.TimestampQpc, row.ObservedAtNs, (ProcessEventKind)row.Kind, row.Pid, row.ParentPid,
                row.IdentityState, row.IdentityKind, row.Generation, (ProcessEventFlags)row.Flags, row.ExitStatus, row.ImageName.Copy());
        }
        return records;
    });

    private T Use<T>(Func<EventFrameView, T> action)
    {
        bool added = false;
        try {
            DangerousAddRef(ref added);
            if (IsInvalid) throw new ObjectDisposedException(nameof(NativeEventFrame));
            var pointer = (EventFrameView*)EventMethods.View(handle);
            if (pointer == null || pointer->AbiVersion != 1 || pointer->State > 10 || pointer->BootState > 7
                || (pointer->Flags & ~3u) != 0 || pointer->Reserved != 0 || pointer->Reserved2 != 0
                || pointer->QpcFrequency < 0 || pointer->EventCount > 512 || pointer->EventCount > 0 && pointer->Events == null)
                throw new InvalidDataException("Pracovník událostí vrátil neplatný rámec.");
            return action(*pointer);
        } finally { if (added) DangerousRelease(); }
    }
    protected override bool ReleaseHandle() { EventMethods.ReleaseFrame(handle); return true; }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct EventRecordView
{
    internal ulong Sequence;
    internal long TimestampQpc, ObservedAtNs;
    internal uint Kind, Pid, ParentPid;
    internal SampleState IdentityState;
    internal GenerationKind IdentityKind;
    internal uint Flags;
    internal ulong Generation;
    internal int ExitStatus;
    internal uint Reserved;
    internal String16 ImageName;
}
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct EventFrameView
{
    internal uint AbiVersion, State, Win32Error, Flags;
    internal ulong BootLow, BootHigh;
    internal uint BootState, Reserved;
    internal long QpcFrequency;
    internal ulong ProviderEventsLost, ProviderBuffersLost, QueueDropped, DecodeErrors, Ambiguous, ResyncVersion;
    internal EventRecordView* Events;
    internal uint EventCount, Reserved2;
}

internal static partial class EventMethods
{
    private const string Library = "nks-monitor.dll";
    [LibraryImport(Library, EntryPoint = "nks_events_create")]
    internal static partial NativeResult Create(out nint monitor);
    [LibraryImport(Library, EntryPoint = "nks_events_start")]
    internal static partial NativeResult Start(NativeEventMonitor monitor);
    [LibraryImport(Library, EntryPoint = "nks_events_stop")]
    internal static partial void Stop(NativeEventMonitor monitor);
    [LibraryImport(Library, EntryPoint = "nks_events_release")]
    internal static partial void ReleaseMonitor(nint monitor);
    [LibraryImport(Library, EntryPoint = "nks_events_read")]
    internal static partial NativeResult Read(NativeEventMonitor monitor, out NativeEventFrame frame);
    [LibraryImport(Library, EntryPoint = "nks_events_ack_resync")]
    internal static partial void Acknowledge(NativeEventMonitor monitor, ulong version);
    [LibraryImport(Library, EntryPoint = "nks_events_frame_view")]
    internal static partial nint View(nint frame);
    [LibraryImport(Library, EntryPoint = "nks_events_frame_release")]
    internal static partial void ReleaseFrame(nint frame);
}
