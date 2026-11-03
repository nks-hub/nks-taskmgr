using Microsoft.UI.Xaml;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static void GraphRefresh()
    {
        const long time = 10_000_000_000;
        var row = new ResourceRow("graph-refresh-network", ResourceKind.Network);
        var receive = new Metric { Value = 2_000_000, State = SampleState.Valid };
        var upload = new Metric { Value = 3_000_000, State = SampleState.Valid };
        row.History.Append(time, receive); row.Upload.Append(time, upload); row.RefreshPreview();
        var preview = row.Preview; var fill = row.PreviewFill; var uploadPreview = row.UploadPreview;
        int notifications = 0;
        row.PropertyChanged += (_, _) => ++notifications;
        row.RefreshPreview();
        Require(notifications == 0 && ReferenceEquals(preview, row.Preview) && ReferenceEquals(fill, row.PreviewFill) && ReferenceEquals(uploadPreview, row.UploadPreview),
            "An unchanged resource history recreated its mini-graph geometries.");
        row.Update("Changed title", "Changed description", "", receive with { Value = 4_000_000 }, upload, default, 10_000_000, time,
            recordHistory: false);
        Require(notifications == 1 && row.Title == "Changed title" && row.Primary.Value == 4_000_000
            && ReferenceEquals(preview, row.Preview), "Mini-graph caching suppressed current resource metadata or values.");
        row.SetStale(true);
        Require(row.Value == "Zastaralá data" && ReferenceEquals(preview, row.Preview), "Mini-graph caching suppressed stale state.");
        int before = notifications; row.RefreshPreview();
        Require(notifications == before, "An already published stale state caused another unchanged refresh notification.");
        row.Upload.Append(time + 250_000_000, upload with { Value = 20_000_000 }); row.RefreshPreview();
        Require(notifications == before + 1 && !ReferenceEquals(uploadPreview, row.UploadPreview) && !ReferenceEquals(preview, row.Preview) && !ReferenceEquals(fill, row.PreviewFill),
            "An upload-only sample failed to update both mini-graphs at their shared scale.");
        preview = row.Preview;
        var replacement = new HistorySeries(); replacement.Append(time, receive);
        before = notifications; row.AttachHistory(replacement, false); row.RefreshPreview();
        Require(notifications == before + 1 && !ReferenceEquals(preview, row.Preview), "A replacement history with the same timestamp retained an obsolete mini-graph.");
        var replacementUpload = new HistorySeries(); replacementUpload.Append(time, upload);
        replacementUpload.Append(time + 250_000_000, upload with { Value = 20_000_000 });
        uploadPreview = row.UploadPreview; before = notifications;
        row.AttachHistory(replacementUpload, true); row.RefreshPreview();
        Require(notifications == before + 1 && !ReferenceEquals(uploadPreview, row.UploadPreview), "Replacing upload history at the same timestamp lost its geometry notification.");
        before = notifications; row.RefreshPreview();
        Require(notifications == before, "Unchanged rebound histories repeated their refresh notification.");

        var memory = new ResourceRow("graph-refresh-memory", ResourceKind.Memory);
        memory.Update("Memory", "", "", new Metric { Value = 50, State = SampleState.Valid }, default, default, 100, time);
        var memoryPreview = memory.Preview;
        memory.Update("Memory", "", "", new Metric { Value = 50, State = SampleState.Valid }, default, default, 200, time,
            recordHistory: false);
        Require(!ReferenceEquals(memoryPreview, memory.Preview), "A capacity change retained a mini-graph at its old scale.");
        var percent = new ResourceRow("graph-refresh-percent", ResourceKind.Cpu);
        percent.Update("CPU", "", "", new Metric { Value = 7, State = SampleState.Valid }, default, default, 100, time);
        Require(percent.Maximum(time, 60) == 100 && memory.Maximum(time, 60) == 200,
            "Percent or memory previews changed their fixed physical scale.");
        HiddenResourceView();
        ResourceNotifications();
        App.Trace("Resource mini-graph cache passed: unchanged geometry, live metadata/stale state, upload, replacement and capacity");
    }

    private static unsafe void HiddenResourceView()
    {
        using var model = new MonitorModel();
        const long time = 100_000_000_000;
        void Apply(int sample) {
            fixed (char* id = "graph-refresh-network", title = "Network", status = "Připojeno") {
                var resource = new ResourceInfo {
                    Kind = ResourceKind.Network, Id = new String16 { Data = id, Length = 21 },
                    Title = new String16 { Data = title, Length = 7 }, Status = new String16 { Data = status, Length = 9 },
                    Primary = new Metric { Value = (sample + 1) * 2_000_000, State = SampleState.Valid },
                    Secondary = new Metric { Value = (sample + 1) * 3_000_000, State = SampleState.Valid }, Capacity = 100_000_000
                };
                long measured = time + sample * 1_000_000_000L;
                model.Apply(new FrameView {
                    Flags = FrameFlags.ProcessData | FrameFlags.ResourceData,
                    Processes = new ProcessData { MeasuredAtNs = measured, LogicalProcessors = 1,
                        CpuPercent = new Metric { Value = 10 + sample * 10, State = SampleState.Valid },
                        Memory = new MemoryInfo { PhysicalBytes = 100, AvailableBytes = (ulong)(50 - sample * 10), State = SampleState.Valid } },
                    Resources = new ResourceData { MeasuredAtNs = measured, ResourceCount = 1, Resources = &resource }
                });
            }
        }
        Apply(0);
        var rows = model.Resources.ToArray();
        Require(rows.Length == 3 && rows.All(row => row.Preview is not null && row.PreviewFill is not null),
            "Resource previews were disabled for existing model consumers by default.");
        var previews = rows.Select(row => (row.Preview, row.PreviewFill, row.UploadPreview)).ToArray();
        var network = rows.Single(row => row.Kind == ResourceKind.Network);
        var cpu = rows.Single(row => row.Kind == ResourceKind.Cpu);
        var memory = rows.Single(row => row.Kind == ResourceKind.Memory);
        int notifications = 0;
        foreach (var row in rows) row.PropertyChanged += (_, _) => ++notifications;
        model.SetResourceViewVisible(false);
        for (int i = 1; i <= 3; ++i) Apply(i);
        Require(notifications == 0 && rows.SequenceEqual(model.Resources)
            && rows.Select((row, i) => ReferenceEquals(previews[i].Preview, row.Preview)
                && ReferenceEquals(previews[i].PreviewFill, row.PreviewFill)
                && ReferenceEquals(previews[i].UploadPreview, row.UploadPreview)).All(value => value),
            "A hidden resource view rebuilt or notified its previews, or lost its current resource rows.");
        Require(cpu.Primary.Value == 40 && memory.Primary.Value == 80 && network.Primary.Value == 8_000_000
            && network.Secondary.Value == 12_000_000 && rows.All(row => row.History.Count == 4)
            && network.Upload.Count == 4 && cpu.History.At(time + 1_000_000_000)?.Value.Value == 20
            && network.Upload.At(time + 2_000_000_000)?.Value.Value == 9_000_000,
            "A hidden resource view dropped current metrics or intermediate history samples.");
        string currentValue = network.Value;
        model.SetResourceViewVisible(true);
        Require(notifications == rows.Length && network.Value == currentValue
            && rows.Select((row, i) => !ReferenceEquals(previews[i].Preview, row.Preview)
                && !ReferenceEquals(previews[i].PreviewFill, row.PreviewFill)).All(value => value)
            && !ReferenceEquals(previews[Array.IndexOf(rows, network)].UploadPreview, network.UploadPreview),
            "Showing the resource view without another tick failed to publish fresh previews and current values.");
        model.SetResourceViewVisible(true);
        Require(notifications == rows.Length, "An unchanged visible resource view repeated its activation refresh.");
        foreach (var row in rows) row.RefreshPreview();
        Require(notifications == rows.Length, "An unchanged refresh after hidden-view activation notified already current resource rows.");
        App.Trace("Hidden resource view passed: live values and complete history, retained geometry, fresh activation without another tick");
    }
}

public sealed partial class ChartView
{
    internal void CheckDiskTransferSeries(HistorySeries read, HistorySeries write)
    {
        if (!ReferenceEquals(series, read) || !ReferenceEquals(secondary, write) || unit != ChartUnit.BytesPerSecond
            || primaryName != "Čtení" || secondaryName != "Zápis")
            throw new InvalidOperationException("Disk transfer graph did not use its collected read/write histories and byte-rate labels.");
    }

    internal void CheckRefreshCache()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        Require(canvas.ActualWidth > 0 && canvas.ActualHeight > 0, "Graph refresh checks require a laid-out visible chart.");
        var previousSeries = series; var previousSecondary = secondary; var previousOverlays = overlays;
        long previousTime = now; int previousSeconds = seconds; double previousMaximum = maximum;
        uint previousColor = color; bool previousGrid = grid, previousFilled = filled;
        string previousName = primaryName, previousSecondName = secondaryName;
        var previousUnit = unit; bool previousAxes = axes, previousInteractive = IsTabStop;
        var previousTimeline = Timeline; double previousWidth = Width;
        try {
            const long time = 100_000_000_000;
            var first = new HistorySeries(); var overlay = new HistorySeries();
            var metric = new Metric { State = SampleState.Valid, Value = 10 };
            first.Append(time - 1_000_000_000, metric); overlay.Append(time - 1_000_000_000, metric);
            var shared = new SharedChartTimeline(); Timeline = shared;
            Configure("Refresh fixture"); SetOverlays([("Overlay", overlay, 0xabb0f1)]);
            Set(first, null, time, 60, 100, 0x80c5c0);
            var primaryGeometry = linePath.Data; var overlayGeometry = overlayPaths[0].Data;
            Configure("Refresh fixture"); Set(first, null, time + 100_000_000, 60, 100, 0x80c5c0);
            Require(ReferenceEquals(primaryGeometry, linePath.Data) && ReferenceEquals(overlayGeometry, overlayPaths[0].Data),
                "An unchanged chart recreated geometry before its time-axis update.");
            overlay.Append(time, metric with { Value = 20 });
            Set(first, null, time + 200_000_000, 60, 100, 0x80c5c0);
            Require(!ReferenceEquals(overlayGeometry, overlayPaths[0].Data) && renderedAt == time + 200_000_000,
                "An overlay-only update was hidden behind the primary history cache.");
            primaryGeometry = linePath.Data;
            first.Append(time + 300_000_000, metric with { Value = 30 });
            Set(first, null, time + 400_000_000, 60, 100, 0x80c5c0);
            Require(!ReferenceEquals(primaryGeometry, linePath.Data), "A new primary point was delayed by the render cache.");
            primaryGeometry = linePath.Data;
            Set(first, null, time + 1_400_000_000, 60, 100, 0x80c5c0);
            Require(!ReferenceEquals(primaryGeometry, linePath.Data) && renderedAt == time + 1_400_000_000,
                "The time axis stopped moving without another sample.");

            Set(first, overlay, time + 1_450_000_000, 60, 100, 0x80c5c0, autoScale: true);
            Require(maximum == 50 && upper.Text == Format(50), "Automatic scaling did not update the cached axis to the measured peak.");
            overlay.Append(time + 1_460_000_000, metric with { Value = 80 });
            Set(first, overlay, time + 1_470_000_000, 60, 100, 0x80c5c0, autoScale: true);
            Require(maximum == 100 && upper.Text == Format(100), "An overlay peak did not expand the automatic scale and cached labels.");

            Configure("Refresh bytes", ChartUnit.Bytes);
            Set(first, overlay, time + 1_500_000_000, 600, 1_073_741_824, 0x80c5c0);
            Require(startTime.Text == "10 min" && upper.Text.Contains("GiB", StringComparison.Ordinal) && legend.Text.Length > 0,
                "Axis caching lost a range, unit, scale or secondary-series change.");
            Configure("Čtení", ChartUnit.BytesPerSecond, secondName: "Zápis");
            Set(first, overlay, time + 1_550_000_000, 600, 2_097_152, 0xdfaf76, area: false);
            Require(upper.Text.Contains("MiB/s", StringComparison.Ordinal) && legend.Text == "Čtení · Zápis"
                && Format(1_048_576).EndsWith(" MiB/s", StringComparison.Ordinal) && plotFrame.BorderThickness.Top == 1,
                "Disk transfer axes retained bit units, network labels or an unframed plot.");
            Configure("Refresh bytes", ChartUnit.Bytes);
            Set(first, overlay, time + 1_500_000_000, 600, 1_073_741_824, 0x80c5c0);
            shared.SetRange(time - 1_000_000_000, time);
            var selection = shared.State;
            Require(intervalBand.Visibility == Visibility.Visible && cursorTime == time, "The shared interval did not reach the rendered chart.");
            Configure("Refresh bytes", ChartUnit.Bytes);
            Set(first, overlay, time + 1_600_000_000, 600, 1_073_741_824, 0x80c5c0);
            Require(shared.State == selection && cursorTime == time && intervalBand.Visibility == Visibility.Visible,
                "An unchanged configuration cleared the shared cursor or interval.");
            Configure("Refresh bytes", ChartUnit.Bytes, showAxes: false, interactive: false);
            Require(scale.Visibility == Visibility.Collapsed && !IsTabStop && !IsHitTestVisible, "A cached configuration ignored axes or interaction changes.");
            Configure("Refresh bytes", ChartUnit.Bytes);
            primaryGeometry = linePath.Data;
            double plotWidth = canvas.ActualWidth;
            Width = Math.Max(80, ActualWidth - 30); UpdateLayout();
            Require(Math.Abs(canvas.ActualWidth - plotWidth) > 1 && !ReferenceEquals(primaryGeometry, linePath.Data),
                "A resized chart retained geometry for its old dimensions.");
            primaryGeometry = linePath.Data; AppearanceChanged();
            Require(!ReferenceEquals(primaryGeometry, linePath.Data), "Appearance invalidation retained an obsolete render cache.");
            App.Trace("Chart refresh cache passed: reuse, primary/overlay updates, moving axis, 600 s scale, shared selection, resize and appearance");
        } finally {
            Width = previousWidth;
            SetOverlays(previousOverlays);
            Configure(previousName, previousUnit, previousAxes, previousSecondName, previousInteractive);
            Set(previousSeries, previousSecondary, previousTime, previousSeconds, previousMaximum, previousColor, previousGrid, previousFilled);
            Timeline = previousTimeline; UpdateLayout();
        }
    }
}

public sealed partial class MainWindow
{
    private void CheckGraphRefresh()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        chart.CheckRefreshCache();
        var buttons = consumerCards.Children.ToArray(); var contents = consumerItems.Select(card => card.Button.Content).ToArray();
        RefreshPerformance();
        Require(buttons.Length == 3 && buttons.SequenceEqual(consumerCards.Children)
            && contents.SequenceEqual(consumerItems.Select(card => card.Button.Content)), "An unchanged performance refresh recreated its process cards.");
        RefreshConsumers(false, false, 0x80c5c0);
        Require(consumers.Children.Count == 0 && consumers.Visibility == Visibility.Collapsed,
            "An unrelated device retained the CPU/RAM process panel.");
        RefreshPerformance();
        Require(buttons.SequenceEqual(consumerCards.Children) && contents.SequenceEqual(consumerItems.Select(card => card.Button.Content)),
            "Returning to CPU/RAM recreated the detached process cards.");
        using var source = new MonitorModel();
        WinUiChecks.ApplyProcesses(source, WinUiChecks.Process(901, 100, 1));
        var old = source.Processes.Single();
        var card = new PerformanceConsumerCard(0, _ => { }); card.Update(old, false, 0x80c5c0);
        Require(ReferenceEquals(card.Current(source), old), "The process card lost its current full key.");
        WinUiChecks.ApplyProcesses(source, WinUiChecks.Process(901, 200, 2));
        Require(card.Current(source) is null, "A process card followed a recycled PID before being rebound.");
        var current = source.Processes.Single(); card.Update(current, false, 0x80c5c0);
        Require(ReferenceEquals(card.Current(source), current) && card.Key == current.Key, "The reused process card retained its old lifetime key.");
        current.SetStatus(true, false);
        Require(card.Current(source) is null, "An ended process card remained selectable.");
        var missing = WinUiChecks.Process(902, 0, 1); missing.GenerationKind = GenerationKind.Missing;
        WinUiChecks.ApplyProcesses(source, missing);
        var unverified = source.Processes.Single(); card.Update(unverified, false, 0x80c5c0);
        Require(ReferenceEquals(card.Current(source), unverified), "An unverified current row was lost before its observed lifetime ended.");
        WinUiChecks.ApplyProcesses(source);
        WinUiChecks.ApplyProcesses(source, missing);
        var replacement = source.Processes.Single();
        Require(!ReferenceEquals(replacement, unverified) && card.Current(source) is null,
            "A process card followed a replacement row with the same missing generation and PID.");
        card.Update(replacement, false, 0x80c5c0);
        Require(ReferenceEquals(card.Current(source), replacement), "A process card could not bind the newly observed row explicitly.");
        App.Trace("Performance process cards passed: persistent controls, bound row/full key, recycled PID, missing generation and ended target rejection");
    }
}
