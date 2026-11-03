using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MonitorModel
{
    private ulong processRevision;
    private bool changesApiMissing;
    private readonly HashSet<ProcessKey> changeKeys = [];
    private readonly HashSet<uint> changeIndices = [];

    private void ApplyFrame(NativeFrame frame, FrameView view)
    {
        void ApplyChanges(ProcessChangesData? changes) {
            bool applied = false;
            if ((view.Flags & FrameFlags.ProcessData) != 0)
                frame.ReadProcessCreationTimes(creation => { Apply(view, creation, false, changes); applied = true; });
            if (!applied) Apply(view, default, false, changes);
        }
        if (!changesApiMissing && (view.Flags & FrameFlags.ProcessData) != 0) {
            try { if (frame.ReadProcessChanges(data => ApplyChanges(data))) return; }
            catch (EntryPointNotFoundException) { changesApiMissing = true; }
            catch (NativeMonitorException) { processRevision = 0; }
        }
        ApplyChanges(null);
    }

    private unsafe bool ApplyProcessChanges(FrameView view, ProcessCreationData creation, ProcessChangesData? optional, out bool orderChanged)
    {
        orderChanged = true;
        if (optional is not { } changes || (changes.Flags & 1) != 0 || changes.BaseRevision != processRevision
            || changes.ProcessCount != view.Processes.ProcessCount) return false;
        changeKeys.Clear(); changeIndices.Clear();
        for (uint i = 0; i < changes.ChangeCount; ++i) {
            var change = changes.Changes[i];
            var key = new ProcessKey(BootIdentity.Identifier, change.Pid, change.GenerationKind, change.Generation);
            if (!changeKeys.Add(key) || (change.Kind == ProcessChangeKind.Added ? processes.ContainsKey(key) : !processes.ContainsKey(key))) return false;
            if (change.Kind != ProcessChangeKind.Removed) {
                if (!changeIndices.Add(change.CurrentIndex) || change.CurrentIndex >= view.Processes.ProcessCount) return false;
                var row = view.Processes.Processes[change.CurrentIndex];
                if (row.Pid != key.Pid || row.GenerationKind != key.Kind || row.Generation != key.Generation) return false;
            }
        }
        orderChanged = false;
        for (uint i = 0; i < changes.ChangeCount; ++i) {
            var change = changes.Changes[i];
            var key = new ProcessKey(BootIdentity.Identifier, change.Pid, change.GenerationKind, change.Generation);
            if (change.Kind == ProcessChangeKind.Removed) {
                processes[key].SetStatus(true, false, visualUpdates); processes.Remove(key); orderChanged = true;
            } else {
                orderChanged |= UpdateProcess(view.Processes.Processes[change.CurrentIndex], key, change.CurrentIndex, creation);
            }
        }
        return true;
    }

    private unsafe bool UpdateProcess(in ProcessInfo source, ProcessKey key, uint index, ProcessCreationData creation)
    {
        bool added = !processes.TryGetValue(key, out var row);
        if (added) processes.Add(key, row = new ProcessRow(key));
        ulong created = key.Kind == GenerationKind.CreationTime ? key.Generation : 0;
        if (index < creation.ProcessCount && creation.Processes != null) {
            var item = creation.Processes[index];
            created = item.Pid == key.Pid && item.Kind == key.Kind && item.Generation == key.Generation && item.State == SampleState.Valid ? item.Created100ns : 0;
        }
        var current = row!;
        bool active = Active(current);
        bool order = added || current.ParentPid != source.ParentPid || current.Created100ns != created || (sortColumn switch {
            1 => false, 2 => !current.Cpu.Value.Equals(source.CpuPercent.Value), 3 => current.WorkingBytes != source.WorkingSetBytes,
            4 => current.PrivateBytes != source.PrivateBytes, 5 => !current.ReadIo.Value.Equals(source.ReadIoBytesPerSecond.Value),
            6 => !current.WriteIo.Value.Equals(source.WriteIoBytesPerSecond.Value), _ => false
        });
        string oldName = current.Name;
        current.Update(source, visualUpdates); current.SetCreatedTime(created);
        return order || !StringComparer.OrdinalIgnoreCase.Equals(oldName, current.Name) || Mode == ProcessMode.Active && active != Active(current);
    }
    private static bool Active(ProcessRow row) => row.Cpu.State == SampleState.Valid && row.Cpu.Value > 0
        || row.ReadIo.State == SampleState.Valid && row.ReadIo.Value > 0 || row.WriteIo.State == SampleState.Valid && row.WriteIo.Value > 0;
}
