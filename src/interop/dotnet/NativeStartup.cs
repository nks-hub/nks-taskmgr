using System.Runtime.InteropServices;

namespace NksTaskMgr.Native;

public enum StartupSource : uint { UserRun, UserRunOnce, MachineRun, MachineRunOnce, MachineRun32, MachineRunOnce32, UserFolder, CommonFolder }
public enum StartupState : uint { Enabled, Disabled, RunOnce }

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct StartupItemInfo
{
    public String16 Name, Command, Location, Target;
    public StartupSource Source;
    public StartupState State;
    public ulong ChangedFileTime;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct StartupView
{
    public uint AbiVersion, ItemCount;
    public StartupItemInfo* Items;
}

public sealed record StartupEntry(StartupSource Source, StartupState State, ulong ChangedFileTime, string Name, string Command, string Location, string Target);

public static partial class NativeStartup
{
    private const string Library = "nks-monitor.dll";

    /// <summary>Reads the startup inventory synchronously; registry, folder and shortcut reads can block, so call it off the UI thread.</summary>
    public static unsafe StartupEntry[] Read()
    {
        NativeMonitor.Check(ReadList(1, out var list));
        try {
            NativeMonitor.Check(GetView(list, (uint)sizeof(StartupView), out var view));
            if (view.AbiVersion != 1 || view.ItemCount > 4096 || view.ItemCount != 0 && view.Items is null) throw new NativeMonitorException(NativeResult.AbiMismatch);
            var result = new StartupEntry[view.ItemCount];
            for (int i = 0; i < result.Length; ++i) {
                var item = view.Items[i];
                if (item.Source > StartupSource.CommonFolder || item.State > StartupState.RunOnce) throw new NativeMonitorException(NativeResult.AbiMismatch);
                result[i] = new(item.Source, item.State, item.ChangedFileTime, item.Name.Copy(), item.Command.Copy(), item.Location.Copy(), item.Target.Copy());
            }
            return result;
        } finally { Release(list); }
    }

    /// <summary>Writes the current user's StartupApproved value like Windows; returns the Windows error code, zero on success.</summary>
    public static uint SetEnabled(StartupSource source, string name, bool enabled)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var result = SetEnabledCore((uint)source, name, checked((uint)name.Length), enabled ? 1 : 0, out uint error);
        if (result == NativeResult.Failed && error != 0) return error;
        NativeMonitor.Check(result);
        return 0;
    }

    [LibraryImport(Library, EntryPoint = "nks_startup_read")]
    private static partial NativeResult ReadList(uint version, out nint list);
    [LibraryImport(Library, EntryPoint = "nks_startup_get_view")]
    private static partial NativeResult GetView(nint list, uint size, out StartupView view);
    [LibraryImport(Library, EntryPoint = "nks_startup_release")]
    private static partial void Release(nint list);
    [LibraryImport(Library, EntryPoint = "nks_startup_set_enabled", StringMarshalling = StringMarshalling.Utf16)]
    private static partial NativeResult SetEnabledCore(uint source, string name, uint length, int enabled, out uint error);
}
