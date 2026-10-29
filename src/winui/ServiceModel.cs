using System.Collections.ObjectModel;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed class ServiceModel : IDisposable
{
    private readonly NativeServices monitor = NativeServices.Open();
    private readonly Dictionary<string, ServiceRow> rows = new(StringComparer.OrdinalIgnoreCase);
    private string selected = "", nativeSelected = "";
    private bool active, paused;
    private int sortColumn;
    private bool descending;
    public ObservableCollection<ServiceRow> Visible { get; } = [];
    public IReadOnlyCollection<ServiceRow> All => rows.Values;
    public string Query { get; private set; } = "";
    public ServiceFilter Filter { get; private set; }
    public ServiceRow? Selected => rows.TryGetValue(selected, out var row) && row.Matches(Filter, Query) ? row : null;
    public ServiceDetailData? Detail { get; private set; }
    public string Error { get; private set; } = "";
    public string DetailError { get; private set; } = "";
    public long Time { get; private set; }
    public bool Stale => !paused && Time > 0 && NativeMonitor.MonotonicNowNs - Time > 3_000_000_000;
    public bool Paused => paused;

    public void SetActivity(bool visible, bool pause)
    {
        if (pause != paused) { monitor.SetPaused(pause); paused = pause; }
        if (visible != active) { monitor.SetActive(visible); active = visible; }
    }
    public void SetQuery(string value) { Query = value; Rebuild(); }
    public void SetFilter(ServiceFilter value) { Filter = value; Rebuild(); }
    public void Sort(int column) { descending = sortColumn == column ? !descending : column == 3; sortColumn = column; Rebuild(); }
    public void Select(string name) { if (rows.ContainsKey(name)) { selected = name; SyncSelection(); } }
    public ServiceRow? Find(string name) => rows.GetValueOrDefault(name);
    public bool Tick()
    {
        if (!active || paused) return false;
        using var frame = monitor.Read();
        if (frame is null) return false;
        frame.Read(Apply); return true;
    }
    private unsafe void Apply(ServiceFrameView view)
    {
        if (view.AbiVersion != 1) throw new NativeMonitorException(NativeResult.AbiMismatch);
        if ((view.Flags & ServiceFrameFlags.ErrorChanged) != 0) Error = view.Error.Copy();
        if ((view.Flags & ServiceFrameFlags.DetailErrorChanged) != 0) DetailError = view.DetailError.Copy();
        if ((view.Flags & ServiceFrameFlags.Data) != 0) {
            if (view.Data.ServiceCount > 16_384) throw new NativeMonitorException(NativeResult.AbiMismatch);
            Time = view.Data.MeasuredAtNs;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            for (uint i = 0; i < view.Data.ServiceCount; ++i) {
                var value = view.Data.Services[i]; string name = value.Name.Copy(); seen.Add(name);
                if (!rows.TryGetValue(name, out var row)) { rows.Add(name, row = new ServiceRow(name)); changed = true; }
                changed |= row.Update(value);
            }
            foreach (string name in rows.Keys.Where(name => !seen.Contains(name)).ToArray()) { rows.Remove(name); changed = true; }
            if (changed) Rebuild();
        }
        if ((view.Flags & ServiceFrameFlags.Detail) != 0 && Selected is { } selectedRow && ServiceConfiguration.Same(selectedRow.Name, view.Detail.Name))
            Detail = ServiceDetailData.Copy(view.Detail);
    }
    private void SyncSelection()
    {
        string name = Selected?.Name ?? "";
        if (StringComparer.OrdinalIgnoreCase.Equals(name, nativeSelected)) return;
        nativeSelected = name; Detail = null; DetailError = ""; monitor.Select(name);
    }
    private void Rebuild()
    {
        var visible = rows.Values.Where(row => row.Matches(Filter, Query)).ToList();
        visible.Sort((a, b) => {
            int value = sortColumn switch { 1 => a.State.CompareTo(b.State), 2 => StringComparer.OrdinalIgnoreCase.Compare(a.Startup, b.Startup),
                3 => a.Pid.CompareTo(b.Pid), 4 => StringComparer.OrdinalIgnoreCase.Compare(a.Account, b.Account), _ => StringComparer.OrdinalIgnoreCase.Compare(a.Title, b.Title) };
            if (value == 0) value = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return descending ? -value : value;
        });
        var wanted = visible.ToHashSet();
        for (int i = Visible.Count - 1; i >= 0; --i) if (!wanted.Contains(Visible[i])) Visible.RemoveAt(i);
        for (int i = 0; i < visible.Count; ++i) {
            if (i < Visible.Count && ReferenceEquals(Visible[i], visible[i])) continue;
            int found = Visible.IndexOf(visible[i]);
            if (found >= 0) Visible.Move(found, i); else Visible.Insert(i, visible[i]);
        }
        SyncSelection();
    }
    public ProcessRow? Host(MonitorModel processes)
    {
        var row = Selected; var detail = Detail; long now = NativeMonitor.MonotonicNowNs;
        if (paused || Stale || Error.Length > 0 || DetailError.Length > 0 || row is null || detail is null || !StringComparer.OrdinalIgnoreCase.Equals(row.Name, detail.Name)
            || row.State != 4 || detail.State != 4 || row.Pid == 0 || row.Pid != detail.Pid || detail.StatusError != 0 || detail.HostError != 0
            || detail.HostCreated == 0 || now - detail.Time > 3_000_000_000 || now - processes.ProcessTime > 3_000_000_000) return null;
        return processes.Processes.FirstOrDefault(candidate => detail.MatchesHost(row, candidate) && processes.IsCurrent(candidate));
    }
    public void Dispose() => monitor.Dispose();
}
