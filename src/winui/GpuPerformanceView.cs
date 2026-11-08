using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Grid gpuPanel = new() { RowSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
    private readonly Grid gpuEngines = new() { RowSpacing = 8, ColumnSpacing = 12 };
    private readonly Grid gpuMemory = new() { RowSpacing = 8, ColumnSpacing = 12 };
    private readonly StackPanel gpuDedicatedTile = new() { Spacing = 4 }, gpuSharedTile = new() { Spacing = 4 };
    private readonly List<(StackPanel Tile, ComboBox Choice, ChartView Chart, TextBlock Value)> gpuTiles = [];
    private readonly ChartView gpuDedicated = new() { Height = 94 }, gpuShared = new() { Height = 94 };
    private readonly TextBlock gpuDedicatedLabel = Label("", 10, 0x95a4ad), gpuSharedLabel = Label("", 10, 0x95a4ad);
    private GpuModel? shownGpu;
    private int shownGpuColumns, shownGpuVersion = -1;
    private bool rebuildingGpu;

    private Grid BuildGpu()
    {
        gpuPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        gpuPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < 4; ++i) {
            var tile = new StackPanel { Spacing = 4 };
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var choice = new ComboBox { FontSize = 11, FontFamily = UiTypography.Font, MinWidth = 0, MinHeight = 25, Height = 25,
                Padding = new Thickness(6, 1, 22, 1), HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(choice, Loc.F("GPU engine {0}", i + 1));
            var value = Label("", 10, 0xcc9bb6); value.HorizontalAlignment = HorizontalAlignment.Right; value.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(value, 1); line.Children.Add(choice); line.Children.Add(value); tile.Children.Add(line);
            var plot = new ChartView { Height = 130, Timeline = performanceTimeline }; tile.Children.Add(plot); gpuTiles.Add((tile, choice, plot, value));
            choice.SelectionChanged += (_, _) => { if (!rebuildingGpu && selectedResource?.Kind == ResourceKind.Gpu) RefreshPerformance(); };
            gpuEngines.Children.Add(tile);
        }
        gpuPanel.Children.Add(gpuEngines);
        gpuDedicatedTile.Children.Add(gpuDedicatedLabel); gpuDedicatedTile.Children.Add(gpuDedicated);
        gpuSharedTile.Children.Add(gpuSharedLabel); gpuSharedTile.Children.Add(gpuShared);
        gpuMemory.Children.Add(gpuDedicatedTile); gpuMemory.Children.Add(gpuSharedTile);
        Grid.SetRow(gpuMemory, 1); gpuPanel.Children.Add(gpuMemory);
        return gpuPanel;
    }

    private void RefreshGpu(ResourceRow resource, long now)
    {
        var gpu = resource.Gpu!;
        int columns = performanceDetailWidth < 640 ? 1 : 2;
        if (!ReferenceEquals(shownGpu, gpu) || shownGpuColumns != columns || shownGpuVersion != gpu.TopologyVersion) {
            shownGpu = gpu; shownGpuColumns = columns; shownGpuVersion = gpu.TopologyVersion;
            var engines = gpu.OrderedEngines().ToArray();
            var defaults = engines.GroupBy(engine => engine.Name).Select(group => group.First()).Take(4).ToArray();
            rebuildingGpu = true;
            gpuEngines.ColumnDefinitions.Clear(); gpuEngines.RowDefinitions.Clear();
            gpuMemory.ColumnDefinitions.Clear(); gpuMemory.RowDefinitions.Clear();
            for (int i = 0; i < columns; ++i) gpuEngines.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < columns; ++i) gpuMemory.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < (2 + columns - 1) / columns; ++i) gpuMemory.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(gpuSharedTile, columns == 2 ? 1 : 0); Grid.SetRow(gpuSharedTile, columns == 2 ? 0 : 1);
            for (int i = 0; i < (4 + columns - 1) / columns; ++i) gpuEngines.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < gpuTiles.Count; ++i) {
                var tile = gpuTiles[i];
                tile.Tile.Visibility = i < engines.Length ? Visibility.Visible : Visibility.Collapsed;
                Grid.SetColumn(tile.Tile, i % columns); Grid.SetRow(tile.Tile, i / columns);
                string? selected = (tile.Choice.SelectedItem as ComboBoxItem)?.Tag as string;
                tile.Choice.Items.Clear();
                foreach (var engine in engines) tile.Choice.Items.Add(new ComboBoxItem { Content = engine.Name + (engines.Count(other => other.Name == engine.Name) > 1 ? " · " + (Array.IndexOf(engines, engine) + 1) : ""), Tag = engine.Id });
                if (engines.Length == 0) tile.Choice.Items.Add(new ComboBoxItem { Content = Loc.T("Engine není dostupný") });
                tile.Choice.IsEnabled = engines.Length > 0;
                tile.Choice.SelectedIndex = selected is not null && engines.Any(engine => engine.Id == selected)
                    ? Array.FindIndex(engines, engine => engine.Id == selected)
                    : defaults.Length > 0 ? Array.IndexOf(engines, defaults[Math.Min(i, defaults.Length - 1)]) : 0;
            }
            gpuEngines.Visibility = engines.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            rebuildingGpu = false;
        }
        foreach (var tile in gpuTiles) {
            string? id = (tile.Choice.SelectedItem as ComboBoxItem)?.Tag as string;
            var engine = id is not null && gpu.Engines.TryGetValue(id, out var found) ? found : null;
            tile.Chart.Height = Math.Clamp(performancePlotHeight * .46, 100, 135);
            tile.Value.Text = resource.Stale ? Loc.T("Zastaralé") : engine is null ? Loc.T("Nedostupné") : Display.Metric(engine.Current, " %");
            tile.Chart.Configure(resource.Title + " · " + (engine?.Name ?? Loc.T("Engine")));
            tile.Chart.Set(engine?.History, null, now, rangeSeconds, 100, 0xcc9bb6, autoScale: scaleChoice.IsChecked == true);
            AutomationProperties.SetName(tile.Chart, Loc.F("Využití GPU {0}: {1}", engine?.Name ?? Loc.T("nedostupné"), tile.Value.Text));
        }
        string MemoryValue(Metric value, double capacity) => resource.Stale ? Loc.T("Zastaralé") : Display.Metric(value, "", 1073741824) + " / " + Display.Number(capacity / 1073741824, " GiB");
        gpuDedicatedLabel.Text = Loc.T("Vyhrazená paměť GPU") + "   " + MemoryValue(resource.Secondary, resource.Capacity);
        gpuSharedLabel.Text = Loc.T("Sdílená paměť GPU") + "   " + MemoryValue(resource.Tertiary, gpu.SharedCapacity);
        gpuDedicated.Configure(Loc.T("Vyhrazená paměť GPU"), ChartUnit.Bytes);
        gpuShared.Configure(Loc.T("Sdílená paměť GPU"), ChartUnit.Bytes);
        gpuDedicated.Set(gpu.Dedicated, null, now, rangeSeconds, Math.Max(1, resource.Capacity), 0xcc9bb6, autoScale: scaleChoice.IsChecked == true);
        gpuShared.Set(gpu.Shared, null, now, rangeSeconds, Math.Max(1, gpu.SharedCapacity), 0xabb0f1, autoScale: scaleChoice.IsChecked == true);
    }

    private void CheckResourceViews()
    {
        var previous = resourceList.SelectedItem;
        foreach (var resource in model.Resources) {
            resourceList.SelectedItem = resource; RefreshPerformance(); root.UpdateLayout(); CheckPerformanceViewport();
            bool expected = resource.Kind is ResourceKind.Cpu or ResourceKind.Memory or ResourceKind.Disk;
            if ((consumers.Visibility == Visibility.Visible) != expected || (!expected && consumers.Children.Count != 0))
                throw new InvalidOperationException("CPU process cards were shown for an unrelated resource.");
            if (resource.Kind == ResourceKind.Disk) {
                if (diskTransferPanel.Visibility != Visibility.Visible || diskTransfers.ActualWidth <= 0)
                    throw new InvalidOperationException("Disk transfer graph did not lay out below disk activity.");
                diskTransfers.CheckDiskTransferSeries(resource.DiskReadHistory, resource.DiskWriteHistory);
                if (diskDetails is null || DiskSummary.Any(name => !factValues.ContainsKey(Loc.T(name))))
                    throw new InvalidOperationException("Disk summary or expanded SMART details did not render.");
                var diskCells = facts.Children.ToArray(); diskDetails.IsExpanded = true; root.UpdateLayout();
                CheckScrollablePerformanceViewport();
                foreach (var detail in diskDetailValues)
                    if (detail.Value.Text != Loc.Native(resource.HardwareFacts[detail.Key])) throw new InvalidOperationException("Disk detail lost a native value.");
                RefreshPerformance();
                if (!diskDetails.IsExpanded || !diskCells.SequenceEqual(facts.Children)) throw new InvalidOperationException("Disk refresh recreated expanded facts.");
                var originalFacts = resource.HardwareFacts;
                try {
                    resource.HardwareFacts = originalFacts.Where(pair => pair.Key != "Teplota").ToDictionary(pair => pair.Key, pair => pair.Value);
                    RefreshPerformance();
                    if (diskDetails is not { IsExpanded: true }) throw new InvalidOperationException("A missing SMART field collapsed the disk details.");
                } finally { resource.HardwareFacts = originalFacts; RefreshPerformance(); }
                if (diskDetails is not { IsExpanded: true }) throw new InvalidOperationException("Restored SMART fields collapsed the disk details.");
                App.Trace($"Disk detail passed: summary={DiskSummary.Length};expanded={diskDetailValues.Count};temperature={factValues[Loc.T("Teplota")].Text};smart={factValues[Loc.T("SMART")].Text}");
                diskDetails.IsExpanded = false;
            }
            if (resource.Kind != ResourceKind.Gpu) continue;
            if (gpuPanel.Visibility != Visibility.Visible || chart.Visibility != Visibility.Collapsed || gpuDedicated.ActualWidth <= 0 || gpuShared.ActualWidth <= 0)
                throw new InvalidOperationException("GPU graphs did not render within their detail.");
            var gpu = resource.Gpu!;
            var cells = facts.Children.ToArray();
            RefreshPerformance();
            if (!cells.SequenceEqual(facts.Children)) throw new InvalidOperationException("Repeated GPU refresh recreated facts.");
            if (gpu.Engines.Count > 0) {
                var choice = gpuTiles[0].Choice; choice.SelectedIndex = choice.Items.Count - 1;
                var id = (string)((ComboBoxItem)choice.SelectedItem).Tag;
                if (!gpu.Engines.ContainsKey(id) || gpu.Engines[id].History.Count == 0) throw new InvalidOperationException("GPU engine selection lost its history.");
                var tile = gpuTiles[0]; long time = NativeMonitor.MonotonicNowNs;
                tile.Chart.Set(gpu.Engines[id].History, null, time, rangeSeconds, 100, 0xcc9bb6);
                var geometry = ((Canvas)tile.Chart.Children[0]).Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().Select(path => path.Data).ToArray();
                tile.Chart.Set(gpu.Engines[id].History, null, time + 1_000_000, rangeSeconds, 100, 0xcc9bb6);
                if (!geometry.SequenceEqual(((Canvas)tile.Chart.Children[0]).Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().Select(path => path.Data)))
                    throw new InvalidOperationException("GPU refresh without a new sample recreated geometry.");
            }
            App.Trace($"GPU graphs passed: {resource.Description};engines={gpu.Engines.Count};dedicated={resource.Secondary.State};shared={resource.Tertiary.State}");
            var unavailable = new ResourceRow("unavailable-gpu", ResourceKind.Gpu);
            RefreshGpu(unavailable, NativeMonitor.MonotonicNowNs);
            if (gpuTiles.Any(tile => ((Canvas)tile.Chart.Children[0]).Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().Any(path => path.Data is not null)))
                throw new InvalidOperationException("Unavailable GPU retained another adapter's graph.");
            RefreshGpu(resource, NativeMonitor.MonotonicNowNs);
        }
        resourceList.SelectedItem = previous; RefreshPerformance();
        App.Trace("Resource context passed: CPU and memory process cards only");
    }
}
