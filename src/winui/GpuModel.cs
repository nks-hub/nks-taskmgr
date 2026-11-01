using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed class GpuEngineRow(string id, string name)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public HistorySeries History { get; internal set; } = new();
    private Metric? current;
    public Metric Current { get => current ?? History.LastValue; internal set => current = value; }
}

public sealed class GpuModel
{
    public Dictionary<string, GpuEngineRow> Engines { get; } = [];
    public int TopologyVersion { get; private set; }
    public double SharedCapacity { get; private set; }
    public Metric Temperature { get; private set; } = new() { State = SampleState.Unavailable, Source = MetricSource.GpuAdapter };
    public HistorySeries Dedicated { get; internal set; } = new();
    public HistorySeries Shared { get; internal set; } = new();

    public unsafe void Apply(GpuInfo info, ResourceRow resource, long time, bool recordHistory = true)
    {
        SharedCapacity = info.SharedCapacity;
        Temperature = info.Temperature;
        if (recordHistory) { Dedicated.Append(time, resource.Secondary); Shared.Append(time, resource.Tertiary); }
        var seen = new HashSet<string>();
        for (uint i = 0; i < info.EngineCount; ++i) {
            var engine = info.Engines[i]; var id = engine.Id.Copy(); seen.Add(id);
            if (!Engines.TryGetValue(id, out var row) && Engines.Count < 64) {
                Engines.Add(id, row = new GpuEngineRow(id, engine.Name.Copy())); ++TopologyVersion;
            }
            if (row is not null) { row.Current = engine.Utilization; if (recordHistory) row.History.Append(time, engine.Utilization); }
        }
        foreach (var row in Engines.Values)
            if (!seen.Contains(row.Id)) {
                row.Current = new Metric { State = SampleState.Unavailable, Source = MetricSource.PerformanceCounter };
                if (recordHistory) row.History.Append(time, row.Current);
            }
    }

    public IEnumerable<GpuEngineRow> OrderedEngines() => Engines.Values.OrderBy(row => row.Name switch {
        "3D" => 0, "Copy" => 1, "VideoDecode" => 2, "VideoEncode" => 3, _ => 4
    }).ThenBy(row => row.Id, StringComparer.Ordinal);
}
