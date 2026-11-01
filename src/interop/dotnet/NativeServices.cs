using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NksTaskMgr.Native;

[Flags]
public enum ServiceFrameFlags : uint { Data = 1, Detail = 2, ErrorChanged = 4, DetailErrorChanged = 8 }

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct ServiceConfigInfo
{
    public uint Error, StartType, DelayedError, Delayed;
    public String16 Account, Group, Command;
    public String16* Dependencies;
    public uint DependencyCount, Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ServiceInfo
{
    public String16 Name, Title;
    public uint State, Pid, Type, AcceptedControls;
    public ServiceConfigInfo Config;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct ServiceData
{
    public long MeasuredAtNs, CollectionNs;
    public ServiceInfo* Services;
    public uint ServiceCount, Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct ServiceDetailInfo
{
    public long MeasuredAtNs;
    public String16 Name, Description;
    public ServiceConfigInfo Config;
    public uint StatusError, DescriptionError, DependentsError, HostError;
    public uint Pid, State;
    public ulong HostCreated100ns;
    public String16* Dependents;
    public uint DependentCount, Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ServiceFrameView
{
    public uint AbiVersion;
    public ServiceFrameFlags Flags;
    public long ReadAtNs;
    public String16 Error, DetailError;
    public ServiceData Data;
    public ServiceDetailInfo Detail;
}

public sealed class NativeServices : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeServices() : base(true) { }

    public static NativeServices Open()
    {
        var result = ServiceMethods.Create(1, out var services);
        if (result == NativeResult.Ok) return services;
        services.Dispose(); throw new NativeMonitorException(result);
    }
    public void SetActive(bool value) => NativeMonitor.Check(ServiceMethods.SetActive(this, value ? 1 : 0));
    public void SetPaused(bool value) => NativeMonitor.Check(ServiceMethods.SetPaused(this, value ? 1 : 0));
    public void Select(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        NativeMonitor.Check(ServiceMethods.Select(this, name, checked((uint)name.Length)));
    }
    public NativeServiceFrame? Read()
    {
        var result = ServiceMethods.Read(this, out var frame);
        if (result == NativeResult.Ok) return frame;
        frame.Dispose();
        if (result == NativeResult.NoData) return null;
        throw new NativeMonitorException(result);
    }
    protected override bool ReleaseHandle() { ServiceMethods.Destroy(handle); return true; }
}

public sealed class NativeServiceFrame : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeServiceFrame() : base(true) { }

    // Copy borrowed strings and arrays before returning from this callback.
    public unsafe void Read(Action<ServiceFrameView> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        Use(pointer => {
            NativeMonitor.Check(ServiceMethods.GetView(pointer, (uint)sizeof(ServiceFrameView), out var view));
            visitor(view);
        });
    }
    public NativeServiceFrame Clone()
    {
        var clone = new NativeServiceFrame();
        Use(pointer => { ServiceMethods.Retain(pointer); clone.SetHandle(pointer); });
        return clone;
    }
    public unsafe ProcessIdentityInfo? ReadHostIdentity(GenerationKind kind)
    {
        ProcessIdentityInfo? identity = null;
        Use(pointer => {
            var status = ServiceMethods.GetHostIdentity(pointer, kind, (uint)sizeof(ProcessIdentityInfo), out var data);
            if (status == NativeResult.NoData) return;
            NativeMonitor.Check(status);
            identity = data;
        });
        return identity;
    }
    private void Use(Action<nint> visitor)
    {
        bool added = false;
        try {
            DangerousAddRef(ref added);
            if (IsInvalid) throw new ObjectDisposedException(nameof(NativeServiceFrame));
            visitor(handle);
        } finally { if (added) DangerousRelease(); }
    }
    protected override bool ReleaseHandle() { ServiceMethods.Release(handle); return true; }
}

internal static partial class ServiceMethods
{
    private const string Library = "nks-monitor.dll";
    [LibraryImport(Library, EntryPoint = "nks_services_create")]
    internal static partial NativeResult Create(uint version, out NativeServices services);
    [LibraryImport(Library, EntryPoint = "nks_services_destroy")]
    internal static partial void Destroy(nint services);
    [LibraryImport(Library, EntryPoint = "nks_services_set_active")]
    internal static partial NativeResult SetActive(NativeServices services, int active);
    [LibraryImport(Library, EntryPoint = "nks_services_set_paused")]
    internal static partial NativeResult SetPaused(NativeServices services, int paused);
    [LibraryImport(Library, EntryPoint = "nks_services_select", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial NativeResult Select(NativeServices services, string name, uint length);
    [LibraryImport(Library, EntryPoint = "nks_services_read")]
    internal static partial NativeResult Read(NativeServices services, out NativeServiceFrame frame);
    [LibraryImport(Library, EntryPoint = "nks_service_frame_get_view")]
    internal static partial NativeResult GetView(nint frame, uint size, out ServiceFrameView view);
    [LibraryImport(Library, EntryPoint = "nks_service_frame_get_host_identity")]
    internal static partial NativeResult GetHostIdentity(nint frame, GenerationKind kind, uint size, out ProcessIdentityInfo identity);
    [LibraryImport(Library, EntryPoint = "nks_service_frame_retain")]
    internal static partial void Retain(nint frame);
    [LibraryImport(Library, EntryPoint = "nks_service_frame_release")]
    internal static partial void Release(nint frame);
}
