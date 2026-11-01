using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class ResourceRow : INotifyPropertyChanged
{
    public IReadOnlyDictionary<string, string> HardwareFacts { get; internal set; } = new Dictionary<string, string>();
    public ResourceRow(string id, ResourceKind kind)
    {
        Id = id; Kind = kind; Gpu = kind == ResourceKind.Gpu ? new GpuModel() : null;
    }
    public string Id { get; }
    public ResourceKind Kind { get; }
    // Older collectors expose only physical interfaces and have no classification fact.
    public bool IsVirtualNetwork => Kind == ResourceKind.Network && HardwareFacts.TryGetValue("Druh rozhraní", out var type) && type == "Virtuální";
    public GpuModel? Gpu { get; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public Metric Primary { get; private set; }
    public Metric Secondary { get; private set; }
    public Metric Tertiary { get; private set; }
    public double Capacity { get; private set; }
    public bool Stale { get; private set; }
    private SolidColorBrush? color;
    public SolidColorBrush Color => color ??= Display.Brush(Display.ResourceColor(Kind));
    public HistorySeries History { get; private set; } = new();
    public HistorySeries Upload { get; private set; } = new();
    internal void AttachHistory(HistorySeries series, bool upload) { if (upload) Upload = series; else History = series; }
    public Geometry? Preview { get; private set; }
    public Geometry? UploadPreview { get; private set; }
    private readonly record struct PreviewState(HistorySeries History, HistorySeries? Upload, long Last, long UploadLast, double Maximum);
    private PreviewState? previewState;
    public string Value => Stale ? "Zastaralá data" : Kind == ResourceKind.Network && Primary.State == SampleState.Valid
        ? "↓ " + Display.Metric(Primary, "", 1_000_000) + "  ↑ " + Display.Metric(Secondary, " Mbit/s", 1_000_000)
        : Kind == ResourceKind.Network && Status == "Odpojeno" ? Status
        : Display.Metric(Primary, Kind == ResourceKind.Memory ? " GiB" : " %", Kind == ResourceKind.Memory ? 1073741824 : 1);
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool SetStale(bool value, bool notify = true)
    {
        if (Stale == value) return false;
        Stale = value; if (notify) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); return true;
    }

    public void Update(string title, string description, string status, Metric primary, Metric secondary, Metric tertiary, double capacity, long time, bool notify = true, bool recordHistory = true)
    {
        Title = title; Description = description; Status = status;
        Primary = primary; Secondary = secondary; Tertiary = tertiary; Capacity = capacity;
        if (recordHistory) {
            History.Append(time, primary);
            if (Kind == ResourceKind.Network) Upload.Append(time, secondary);
        }
        if (notify) RefreshPreview();
    }

    public void RefreshPreview()
    {
        long time = History.LastTime;
        double maximum = Maximum(time, 60);
        var state = new PreviewState(History, Kind == ResourceKind.Network ? Upload : null, time,
            Kind == ResourceKind.Network ? Upload.LastTime : 0, maximum);
        if (previewState != state) {
            Preview = History.Build(50, 31, time, 60, maximum);
            if (Kind == ResourceKind.Network) UploadPreview = Upload.Build(50, 31, time, 60, maximum);
            previewState = state;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public double Maximum(long now, int seconds)
    {
        if (Kind == ResourceKind.Memory) return Math.Max(1, Capacity);
        if (Kind != ResourceKind.Network) return 100;
        long start = now - seconds * 1_000_000_000L;
        return Math.Max(History.Maximum(start, 1_000_000), Upload.Maximum(start, 1_000_000));
    }
}
