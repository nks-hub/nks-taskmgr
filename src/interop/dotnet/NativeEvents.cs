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
public enum ProcessTrafficAttribution : uint { Unknown, Correlated, Direct }
public enum ProcessTrafficSource : uint { PhysicalDisk = 1, Tcp = 2, Udp = 3 }
public enum ProcessTrafficDirection : uint { Read = 1, Write = 2, Sent = 3, Received = 4 }
[Flags]
public enum ProcessTrafficFlags : uint { Partial = 1, Ipv6 = 2, CounterOverflow = 4, Late = 8, IdentityConflict = 16, WindowOpen = 32 }

public readonly record struct ProcessEventInfo(ulong Sequence, long TimestampQpc, long ObservedAtNs, ProcessEventKind Kind,
    uint Pid, uint ParentPid, SampleState IdentityState, GenerationKind IdentityKind, ulong Generation,
    ProcessEventFlags Flags, int ExitStatus, string ImageName);
public readonly record struct ProcessEventBatchInfo(ProcessEventMonitorState State, uint Win32Error, ProcessEventFrameFlags Flags,
    BootIdentifier Boot, SampleState BootState, long QpcFrequency, ulong ProviderEventsLost, ulong ProviderBuffersLost,
    ulong QueueDropped, ulong DecodeErrors, ulong Ambiguous, ulong ResyncVersion);
public readonly record struct ProcessTrafficSample(ulong SampleId, long StartQpc, long EndQpc, long ObservedAtNs,
    uint Pid, GenerationKind IdentityKind, ulong Generation, SampleState IdentityState, ProcessTrafficAttribution Attribution,
    ProcessTrafficSource Source, ProcessTrafficDirection Direction, ulong ObservedBytes, ulong Operations, double ObservedBytesPerSecond,
    SampleState State, ProcessTrafficFlags Flags, ulong AttributionEpoch, MeasurementMetadata Measurement);
public readonly record struct ProcessTrafficBatchInfo(SampleState DiskState, SampleState NetworkState, SampleState IdentityProviderState,
    uint Flags, long ObservedAtNs, ulong AttributionEpoch, ulong UnknownEvents, ulong UnknownBytes, ulong EvictedSamples,
    ulong PendingDropped, ulong LateEvents, ulong IdentityConflicts, ulong BudgetBytesUsed, ulong BudgetLimit);
public sealed record ProcessTrafficBatch(ProcessTrafficBatchInfo Info, ProcessTrafficSample[] Samples);

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
        if (result == NativeResult.NoData) throw new InvalidOperationException("Je zadrženo příliš mnoho monitorů nebo rámců událostí.");
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
            bool identityValid = row.IdentityState == SampleState.Valid && row.IdentityKind == GenerationKind.CreationTime
                && row.Generation != 0 && row.Pid != 0 && view.BootState == (uint)SampleState.Valid && (view.BootLow | view.BootHigh) != 0;
            bool identityMissing = row.IdentityState == SampleState.Unavailable && row.IdentityKind == GenerationKind.Missing && row.Generation == 0;
            if (row.Sequence == 0 || row.TimestampQpc < 0 || row.ObservedAtNs < 0 || row.Kind is < 1 or > 2
                || !identityValid && !identityMissing || (row.Flags & ~15u) != 0 || row.Reserved != 0 || row.ImageName.Reserved != 0
                || (row.Flags & 1) == 0 && row.ExitStatus != 0 || (row.Flags & 8) == 0 && row.ParentPid != 0
                || (row.Flags & 2) == 0 && row.ImageName.Length != 0
                || row.ImageName.Length > 1024 || row.ImageName.Length > 0 && row.ImageName.Data == null)
                throw new InvalidDataException("Pracovník událostí vrátil neplatný záznam.");
            records[i] = new(row.Sequence, row.TimestampQpc, row.ObservedAtNs, (ProcessEventKind)row.Kind, row.Pid, row.ParentPid,
                row.IdentityState, row.IdentityKind, row.Generation, (ProcessEventFlags)row.Flags, row.ExitStatus, row.ImageName.Copy());
        }
        return records;
    });

    public ProcessTrafficBatch CopyTraffic() => Use(view => {
        if (EventMethods.Traffic(handle, (uint)sizeof(TrafficFrameView), out var traffic) != NativeResult.Ok
            || traffic.AbiVersion != 1 || traffic.SampleCount > 256 || traffic.SampleCount > 0 && traffic.Samples == null
            || traffic.DiskState > SampleState.Gap || traffic.NetworkState > SampleState.Gap || traffic.IdentityProviderState > SampleState.Gap
            || traffic.Flags != 0 || traffic.ObservedAtNs < 0 || traffic.BudgetLimit != 8 * 1024 * 1024 || traffic.BudgetBytesUsed > traffic.BudgetLimit)
            throw new InvalidDataException("Pracovník událostí vrátil neplatný rámec přenosů.");
        var samples = new ProcessTrafficSample[traffic.SampleCount];
        var ids = new HashSet<ulong>();
        for (int i = 0; i < samples.Length; ++i) {
            var row = traffic.Samples[i];
            bool bootValid = view.BootState == (uint)SampleState.Valid && (view.BootLow | view.BootHigh) != 0;
            bool missing = row.Attribution == ProcessTrafficAttribution.Unknown && row.IdentityState == SampleState.Unavailable
                && row.IdentityKind == GenerationKind.Missing && row.Generation == 0;
            bool candidate = row.IdentityKind == GenerationKind.CreationTime && row.Generation != 0 && row.Pid != 0 && bootValid;
            bool correlated = candidate && row.Attribution == ProcessTrafficAttribution.Correlated && row.IdentityState == SampleState.Unavailable;
            bool direct = candidate && row.Attribution == ProcessTrafficAttribution.Direct && row.IdentityState == SampleState.Valid;
            bool disk = row.Source == ProcessTrafficSource.PhysicalDisk;
            var m = row.Measurement;
            if (!ids.Add(row.SampleId) || row.SampleId == 0 || row.StartQpc < 0 || row.EndQpc <= row.StartQpc
                || view.QpcFrequency <= 0 || row.EndQpc - row.StartQpc != view.QpcFrequency || row.ObservedAtNs <= 0
                || (!missing && !correlated && !direct) || row.Source is < ProcessTrafficSource.PhysicalDisk or > ProcessTrafficSource.Udp
                || (disk ? row.Direction is < ProcessTrafficDirection.Read or > ProcessTrafficDirection.Write || (row.Flags & ProcessTrafficFlags.Ipv6) != 0
                    : row.Direction is < ProcessTrafficDirection.Sent or > ProcessTrafficDirection.Received)
                || row.Operations == 0 || !double.IsFinite(row.ObservedBytesPerSecond) || row.ObservedBytesPerSecond < 0
                || row.State is not (SampleState.Valid or SampleState.Gap) || (row.Flags & ~(ProcessTrafficFlags)63) != 0
                || (!direct || row.State != SampleState.Valid) && (row.Flags & ProcessTrafficFlags.Partial) == 0
                || row.AttributionEpoch == 0 || row.AttributionEpoch > traffic.AttributionEpoch || !m.IsValid
                || m.Unit != MeasurementUnit.BytesPerSecond || m.Normalization != MeasurementNormalization.None || m.NormalizationDivisor != 1
                || m.Basis != MeasurementBasis.ElapsedDelta || m.IntervalState != MeasurementIntervalState.Measured || m.IntervalNs != 1_000_000_000
                || m.ObservedAtNs != row.ObservedAtNs || m.CadenceNs != 0)
                throw new InvalidDataException("Pracovník událostí vrátil neplatný přenos.");
            samples[i] = new(row.SampleId, row.StartQpc, row.EndQpc, row.ObservedAtNs, row.Pid, row.IdentityKind, row.Generation,
                row.IdentityState, row.Attribution, row.Source, row.Direction, row.ObservedBytes, row.Operations, row.ObservedBytesPerSecond,
                row.State, row.Flags, row.AttributionEpoch, m);
        }
        return new ProcessTrafficBatch(new(traffic.DiskState, traffic.NetworkState, traffic.IdentityProviderState, traffic.Flags,
            traffic.ObservedAtNs, traffic.AttributionEpoch, traffic.UnknownEvents, traffic.UnknownBytes, traffic.EvictedSamples,
            traffic.PendingDropped, traffic.LateEvents, traffic.IdentityConflicts, traffic.BudgetBytesUsed, traffic.BudgetLimit), samples);
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

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct TrafficRecordView
{
    internal ulong SampleId;
    internal long StartQpc, EndQpc, ObservedAtNs;
    internal uint Pid;
    internal GenerationKind IdentityKind;
    internal ulong Generation;
    internal SampleState IdentityState;
    internal ProcessTrafficAttribution Attribution;
    internal ProcessTrafficSource Source;
    internal ProcessTrafficDirection Direction;
    internal ulong ObservedBytes, Operations;
    internal double ObservedBytesPerSecond;
    internal SampleState State;
    internal ProcessTrafficFlags Flags;
    internal ulong AttributionEpoch;
    internal MeasurementMetadata Measurement;
}
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct TrafficFrameView
{
    internal uint AbiVersion, SampleCount;
    internal TrafficRecordView* Samples;
    internal SampleState DiskState, NetworkState, IdentityProviderState;
    internal uint Flags;
    internal long ObservedAtNs;
    internal ulong AttributionEpoch, UnknownEvents, UnknownBytes, EvictedSamples, PendingDropped, LateEvents, IdentityConflicts, BudgetBytesUsed, BudgetLimit;
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
    [LibraryImport(Library, EntryPoint = "nks_events_frame_get_traffic")]
    internal static partial NativeResult Traffic(nint frame, uint viewSize, out TrafficFrameView view);
    [LibraryImport(Library, EntryPoint = "nks_events_frame_release")]
    internal static partial void ReleaseFrame(nint frame);
}
