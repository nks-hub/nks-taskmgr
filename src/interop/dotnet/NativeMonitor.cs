using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: DisableRuntimeMarshalling]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]

namespace NksTaskMgr.Native;

public enum NativeResult { Ok, NoData, InvalidArgument, AbiMismatch, OutOfMemory, Failed }
public enum SampleState : uint { Valid, First, Unavailable, AccessDenied, Unsupported, Stale, Error, Gap }
public enum GenerationKind : uint { Missing, CreationTime, SequenceNumber }
public enum ResourceKind : uint { Cpu, Memory, Disk, Gpu, Network }
public enum MetricSource : uint { NativeProcess, ProcessorTime, PerformanceInfo, PerformanceCounter, NetworkInterface, Dxgi, GpuAdapter }
public readonly record struct BootIdentifier(ulong Low, ulong High)
{
    public bool IsEmpty => Low == 0 && High == 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct BootIdentityInfo
{
    public ulong Low, High;
    public SampleState State;
    public uint Reserved;
    public readonly BootIdentifier Identifier => new(Low, High);
}
[Flags]
public enum FrameFlags : uint { ProcessData = 1, ResourceData = 2, ProcessErrorChanged = 4, ResourceErrorChanged = 8 }

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct String16
{
    public char* Data;
    public uint Length, Reserved;
    public readonly string Copy() => Length == 0 ? string.Empty : new string(Data, 0, checked((int)Length));
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct Metric
{
    public double Value;
    public SampleState State;
    public MetricSource Source;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ProcessInfo
{
    public ulong BootTime;
    public uint Pid, ParentPid;
    public ulong Generation;
    public GenerationKind GenerationKind;
    public uint Threads, Handles, Reserved;
    public ulong WorkingSetBytes, PrivateBytes, Kernel100ns, User100ns, ReadIoBytes, WriteIoBytes;
    public Metric CpuPercent, ReadIoBytesPerSecond, WriteIoBytesPerSecond;
    public String16 Name;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ProcessCreationInfo
{
    public uint Pid;
    public GenerationKind Kind;
    public ulong Generation, Created100ns;
    public SampleState State;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct ProcessCreationData
{
    public ProcessCreationInfo* Processes;
    public uint ProcessCount, Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ProcessIdentityInfo
{
    public ulong BootLow, BootHigh;
    public uint Pid;
    public GenerationKind Kind;
    public ulong Generation;
    public SampleState State;
    public uint Reserved;
    public readonly BootIdentifier Boot => new(BootLow, BootHigh);
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ProcessorInfo
{
    public uint Group, Index;
    public ulong Kernel100ns, User100ns, Idle100ns;
    public Metric CpuPercent;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct MemoryInfo
{
    public ulong PhysicalBytes, AvailableBytes, CommittedBytes, CommitLimitBytes;
    public ulong CacheBytes, PagedPoolBytes, NonpagedPoolBytes;
    public ulong PageFileBytes, PageFileUsedBytes, PageFilePeakBytes, InstalledBytes;
    public SampleState State, PageFileState;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct ProcessData
{
    public long MeasuredAtNs, WallTimeUnixNs, CollectionNs, QueryNs, ParseNs, SystemNs;
    public uint LogicalProcessors;
    public SampleState ProcessorState;
    public Metric CpuPercent;
    public String16 CpuModel;
    public MemoryInfo Memory;
    public ProcessInfo* Processes;
    public uint ProcessCount, ProcessorCount;
    public ProcessorInfo* Processors;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ResourceInfo
{
    public ResourceKind Kind;
    public uint Reserved;
    public String16 Id, Title, Description, Status;
    public Metric Primary, Secondary, Tertiary;
    public double Capacity;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct ResourceData
{
    public long MeasuredAtNs;
    public uint DiskCountersAvailable, GpuCountersAvailable;
    public ResourceInfo* Resources;
    public uint ResourceCount, Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct FrameView
{
    public uint AbiVersion;
    public FrameFlags Flags;
    public long ReadAtNs;
    public String16 ProcessError, ResourceError;
    public ProcessData Processes;
    public ResourceData Resources;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct GpuEngineInfo
{
    public String16 Id, Name;
    public Metric Utilization;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct GpuInfo
{
    public String16 ResourceId;
    public double SharedCapacity;
    public Metric Temperature;
    public GpuEngineInfo* Engines;
    public uint EngineCount, Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct GpuData
{
    public GpuInfo* Gpus;
    public uint GpuCount, Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct HardwareFactInfo { public String16 ResourceId, Name, Value; }

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct HardwareData { public HardwareFactInfo* Facts; public uint FactCount, Reserved; }

public sealed class NativeMonitorException(NativeResult result) : Exception($"Native monitor returned {result}.")
{
    public NativeResult Result { get; } = result;
}

public sealed class NativeMonitor : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeMonitor() : base(true) { }

    public static NativeMonitor Open(uint intervalMilliseconds = 1000, bool paused = false)
    {
        var result = NativeMethods.Create(1, intervalMilliseconds, paused ? 1 : 0, out var monitor);
        if (result == NativeResult.Ok) return monitor;
        monitor.Dispose();
        throw new NativeMonitorException(result);
    }

    public void SetPaused(bool value) => Check(NativeMethods.SetPaused(this, value ? 1 : 0));
    public void SetInterval(uint milliseconds) => Check(NativeMethods.SetInterval(this, milliseconds));
    public static long MonotonicNowNs => NativeMethods.MonotonicNowNs();

    public static unsafe ProcessIdentityInfo QueryProcessIdentity(SafeProcessHandle process, GenerationKind kind)
    {
        ArgumentNullException.ThrowIfNull(process);
        Check(NativeMethods.QueryProcessIdentity(process, kind, (uint)sizeof(ProcessIdentityInfo), out var identity));
        return identity;
    }

    public NativeFrame? Read()
    {
        var result = NativeMethods.Read(this, out var frame);
        if (result == NativeResult.Ok) return frame;
        frame.Dispose();
        if (result == NativeResult.NoData) return null;
        throw new NativeMonitorException(result);
    }

    internal static void Check(NativeResult result)
    {
        if (result != NativeResult.Ok) throw new NativeMonitorException(result);
    }

    protected override bool ReleaseHandle() { NativeMethods.Destroy(handle); return true; }
}

public sealed partial class NativeFrame : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeFrame() : base(true) { }

    // Borrowed arrays and strings may only be read inside this callback.
    public unsafe void Read(Action<FrameView> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        Use(pointer => {
            NativeMonitor.Check(NativeMethods.GetView(pointer, (uint)sizeof(FrameView), out var view));
            visitor(view);
        });
    }

    public NativeFrame Clone()
    {
        var clone = new NativeFrame();
        Use(pointer => { NativeMethods.Retain(pointer); clone.SetHandle(pointer); });
        return clone;
    }

    public unsafe void ReadGpu(Action<GpuData> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        Use(pointer => {
            NativeMonitor.Check(NativeMethods.GetGpuData(pointer, (uint)sizeof(GpuData), out var data));
            visitor(data);
        });
    }

    public unsafe void ReadHardware(Action<HardwareData> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        Use(pointer => {
            NativeMonitor.Check(NativeMethods.GetHardwareData(pointer, (uint)sizeof(HardwareData), out var data));
            visitor(data);
        });
    }

    public unsafe BootIdentityInfo? ReadBootIdentity()
    {
        BootIdentityInfo? identity = null;
        Use(pointer => {
            var status = NativeMethods.GetBootData(pointer, (uint)sizeof(BootIdentityInfo), out var data);
            if (status == NativeResult.NoData) return;
            NativeMonitor.Check(status);
            identity = data;
        });
        return identity;
    }

    public unsafe void ReadProcessCreationTimes(Action<ProcessCreationData> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        Use(pointer => {
            var status = NativeMethods.GetProcessCreationData(pointer, (uint)sizeof(ProcessCreationData), out var data);
            if (status == NativeResult.NoData) return;
            NativeMonitor.Check(status);
            visitor(data);
        });
    }

    private void Use(Action<nint> action)
    {
        bool added = false;
        try {
            DangerousAddRef(ref added);
            if (IsInvalid) throw new ObjectDisposedException(nameof(NativeFrame));
            action(handle);
        } finally {
            if (added) DangerousRelease();
        }
    }

    protected override bool ReleaseHandle() { NativeMethods.Release(handle); return true; }
}

internal static partial class NativeMethods
{
    private const string Library = "nks-monitor.dll";
    [LibraryImport(Library, EntryPoint = "nks_monitor_create")]
    internal static partial NativeResult Create(uint abiVersion, uint intervalMs, int paused, out NativeMonitor monitor);
    [LibraryImport(Library, EntryPoint = "nks_monitor_destroy")]
    internal static partial void Destroy(nint monitor);
    [LibraryImport(Library, EntryPoint = "nks_monitor_set_paused")]
    internal static partial NativeResult SetPaused(NativeMonitor monitor, int paused);
    [LibraryImport(Library, EntryPoint = "nks_monitor_set_interval")]
    internal static partial NativeResult SetInterval(NativeMonitor monitor, uint intervalMs);
    [LibraryImport(Library, EntryPoint = "nks_monitor_read")]
    internal static partial NativeResult Read(NativeMonitor monitor, out NativeFrame frame);
    [LibraryImport(Library, EntryPoint = "nks_frame_get_view")]
    internal static partial NativeResult GetView(nint frame, uint viewSize, out FrameView view);
    [LibraryImport(Library, EntryPoint = "nks_frame_get_gpu_data")]
    internal static partial NativeResult GetGpuData(nint frame, uint dataSize, out GpuData data);
    [LibraryImport(Library, EntryPoint = "nks_frame_get_hardware_data")]
    internal static partial NativeResult GetHardwareData(nint frame, uint dataSize, out HardwareData data);
    [LibraryImport(Library, EntryPoint = "nks_frame_get_boot_data")]
    internal static partial NativeResult GetBootData(nint frame, uint dataSize, out BootIdentityInfo data);
    [LibraryImport(Library, EntryPoint = "nks_frame_get_process_creation_data")]
    internal static partial NativeResult GetProcessCreationData(nint frame, uint dataSize, out ProcessCreationData data);
    [LibraryImport(Library, EntryPoint = "nks_process_query_identity")]
    internal static partial NativeResult QueryProcessIdentity(SafeProcessHandle process, GenerationKind kind, uint dataSize, out ProcessIdentityInfo data);
    [LibraryImport(Library, EntryPoint = "nks_frame_retain")]
    internal static partial void Retain(nint frame);
    [LibraryImport(Library, EntryPoint = "nks_frame_release")]
    internal static partial void Release(nint frame);
    [LibraryImport(Library, EntryPoint = "nks_monotonic_now_ns")]
    internal static partial long MonotonicNowNs();
}
