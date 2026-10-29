using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed class GpuEngineRow(string id, string name)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public HistorySeries History { get; } = new();
    public Metric Current => History.LastValue;
}

public sealed class GpuModel
{
    public Dictionary<string, GpuEngineRow> Engines { get; } = [];
    public double SharedCapacity { get; private set; }
    public Metric Temperature { get; private set; } = new() { State = SampleState.Unavailable, Source = MetricSource.GpuAdapter };
    public HistorySeries Dedicated { get; } = new();
    public HistorySeries Shared { get; } = new();

    public unsafe void Apply(GpuInfo info, ResourceRow resource, long time)
    {
        SharedCapacity = info.SharedCapacity;
        Temperature = info.Temperature;
        Dedicated.Append(time, resource.Secondary); Shared.Append(time, resource.Tertiary);
        var seen = new HashSet<string>();
        for (uint i = 0; i < info.EngineCount; ++i) {
            var engine = info.Engines[i]; var id = engine.Id.Copy(); seen.Add(id);
            if (!Engines.TryGetValue(id, out var row) && Engines.Count < 64) Engines.Add(id, row = new GpuEngineRow(id, engine.Name.Copy()));
            row?.History.Append(time, engine.Utilization);
        }
        foreach (var row in Engines.Values)
            if (!seen.Contains(row.Id)) row.History.Append(time, new Metric { State = SampleState.Unavailable, Source = MetricSource.PerformanceCounter });
    }

    public IEnumerable<GpuEngineRow> OrderedEngines() => Engines.Values.OrderBy(row => row.Name switch {
        "3D" => 0, "Copy" => 1, "VideoDecode" => 2, "VideoEncode" => 3, _ => 4
    }).ThenBy(row => row.Id, StringComparer.Ordinal);
}
