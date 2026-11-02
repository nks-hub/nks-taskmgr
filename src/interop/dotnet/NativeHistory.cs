using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NksTaskMgr.Native;

public enum HistoryChannel : uint { Primary, Upload, Dedicated, Shared, Engine, Processor, PageFile, DiskRead, DiskWrite }

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct HistoryDefinitionInfo
{
    public uint Id;
    public HistoryChannel Channel;
    public uint Group, Index;
    public String16 ResourceId, DetailId, Name;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct CollectedHistoryPoint
{
    public ulong Sequence;
    public long TimeNs;
    public uint Series, Reserved;
    public Metric Metric;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct HistoryView
{
    public uint AbiVersion, Reserved;
    public ulong LostPoints, CaptureErrors;
    public HistoryDefinitionInfo* Definitions;
    public uint DefinitionCount, Flags;
    public CollectedHistoryPoint* Points;
    public uint PointCount, MorePending;
}

public static class NativeHistory
{
    public static NativeHistoryFrame? Read(NativeMonitor monitor)
    {
        var status = HistoryMethods.Read(monitor, out var frame);
        if (status == NativeResult.Ok) return frame;
        frame.Dispose();
        if (status == NativeResult.NoData) return null;
        throw new NativeMonitorException(status);
    }
}

public sealed class NativeHistoryFrame : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeHistoryFrame() : base(true) { }

    public unsafe void Read(Action<HistoryView> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        bool added = false;
        try {
            DangerousAddRef(ref added);
            if (IsInvalid) throw new ObjectDisposedException(nameof(NativeHistoryFrame));
            NativeMonitor.Check(HistoryMethods.GetView(handle, (uint)sizeof(HistoryView), out var view));
            if (view.AbiVersion != 2 || view.DefinitionCount > 512 || view.PointCount > 4096
                || (view.DefinitionCount != 0 && view.Definitions == null) || (view.PointCount != 0 && view.Points == null))
                throw new NativeMonitorException(NativeResult.AbiMismatch);
            visitor(view);
        } finally { if (added) DangerousRelease(); }
    }

    protected override bool ReleaseHandle() { HistoryMethods.Release(handle); return true; }
}

internal static partial class HistoryMethods
{
    [LibraryImport("nks-monitor.dll", EntryPoint = "nks_monitor_read_history")]
    internal static partial NativeResult Read(NativeMonitor monitor, out NativeHistoryFrame frame);
    [LibraryImport("nks-monitor.dll", EntryPoint = "nks_history_frame_get_view")]
    internal static partial NativeResult GetView(nint frame, uint size, out HistoryView view);
    [LibraryImport("nks-monitor.dll", EntryPoint = "nks_history_frame_release")]
    internal static partial void Release(nint frame);
}
