using System.Runtime.InteropServices;

namespace NksTaskMgr.Native;

/// <summary>How the machine-wide Task Manager registration relates to a GUI path.</summary>
public enum ReplacementStatus : uint { NotRegistered, ThisInstall, OtherNks, Foreign }

/// <summary>State of the taskmgr.exe Image File Execution Options registration; <see cref="Error"/> is a Win32 code, 0 when read.</summary>
public sealed record ReplacementState(ReplacementStatus Status, bool HasBackup, bool BackupRestorable, uint Error, string Debugger);

public static partial class NativeReplacement
{
    private const int DebuggerCapacity = 1024;

    /// <summary>Reads the registration from HKLM without elevation, relative to the absolute path of the running GUI image.</summary>
    public static ReplacementState Read(string guiPath)
    {
        var buffer = new char[DebuggerCapacity];
        NativeMonitor.Check(ReplacementMethods.Read(guiPath, out uint status, out uint flags, out uint error, buffer, DebuggerCapacity, out uint length));
        if (status > (uint)ReplacementStatus.Foreign) throw new NativeMonitorException(NativeResult.Failed);
        string debugger = new(buffer, 0, (int)Math.Min(length, DebuggerCapacity));
        return new((ReplacementStatus)status, (flags & 1) != 0, (flags & 2) != 0, error, length > DebuggerCapacity ? debugger + "…" : debugger);
    }
}

internal static partial class ReplacementMethods
{
    [LibraryImport("nks-monitor.dll", EntryPoint = "nks_replacement_read", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial NativeResult Read(string guiPath, out uint status, out uint flags, out uint error,
        [Out] char[] debugger, uint capacity, out uint length);
}
