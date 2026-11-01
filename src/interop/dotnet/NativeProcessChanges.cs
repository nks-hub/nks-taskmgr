using System.Runtime.InteropServices;

namespace NksTaskMgr.Native;

public enum ProcessChangeKind : uint { Added = 1, Updated = 2, Removed = 3 }
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ProcessChange
{
    public ProcessChangeKind Kind;
    public uint CurrentIndex, Pid;
    public GenerationKind GenerationKind;
    public ulong Generation;
}
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct ProcessChangesData
{
    public uint AbiVersion, Flags;
    public ulong Revision, BaseRevision;
    public ProcessChange* Changes;
    public uint ChangeCount, ProcessCount;
}

public sealed partial class NativeFrame
{
    public unsafe bool ReadProcessChanges(Action<ProcessChangesData> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        bool found = false;
        Use(pointer => {
            var result = NativeMethods.GetProcessChanges(pointer, (uint)sizeof(ProcessChangesData), out var data);
            if (result == NativeResult.NoData) return;
            NativeMonitor.Check(result);
            if (data.AbiVersion != 1 || (data.Flags & ~1u) != 0 || data.Revision == 0 || data.ProcessCount > 131072
                || data.ChangeCount > 262144 || data.ChangeCount != 0 && data.Changes == null
                || (data.Flags & 1) == 0 && data.BaseRevision >= data.Revision) throw new NativeMonitorException(NativeResult.AbiMismatch);
            for (uint i = 0; i < data.ChangeCount; ++i) {
                var row = data.Changes[i];
                if (row.Kind is < ProcessChangeKind.Added or > ProcessChangeKind.Removed || row.GenerationKind > GenerationKind.SequenceNumber
                    || (row.Kind == ProcessChangeKind.Removed ? row.CurrentIndex != uint.MaxValue : row.CurrentIndex >= data.ProcessCount))
                    throw new NativeMonitorException(NativeResult.AbiMismatch);
            }
            visitor(data); found = true;
        });
        return found;
    }
}

internal static partial class NativeMethods
{
    [LibraryImport(Library, EntryPoint = "nks_frame_get_process_changes")]
    internal static partial NativeResult GetProcessChanges(nint frame, uint size, out ProcessChangesData data);
}
