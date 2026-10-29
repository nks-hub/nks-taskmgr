using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Grid gpuPanel = new() { RowSpacing = 14, Margin = new Thickness(0, 0, 0, 10) };
    private readonly Grid gpuEngines = new() { RowSpacing = 12, ColumnSpacing = 14 };
    private readonly List<(StackPanel Tile, ComboBox Choice, ChartView Chart, TextBlock Value)> gpuTiles = [];
    private readonly ChartView gpuDedicated = new() { Height = 72 }, gpuShared = new() { Height = 72 };
    private readonly TextBlock gpuDedicatedLabel = Label("", 10, 0x95a4ad), gpuSharedLabel = Label("", 10, 0x95a4ad);
    private string gpuLayout = "";
    private bool rebuildingGpu;

    private Grid BuildGpu()
    {
        gpuPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        gpuPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < 4; ++i) {
            var tile = new StackPanel { Spacing = 5 };
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            var choice = new ComboBox { FontSize = 10, MinHeight = 27, Padding = new Thickness(6, 1, 22, 1), HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(choice, "GPU engine " + (i + 1));
            var value = Label("", 10, 0xcc9bb6); value.HorizontalAlignment = HorizontalAlignment.Right; value.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(value, 1); line.Children.Add(choice); line.Children.Add(value); tile.Children.Add(line);
            var plot = new ChartView { Height = 104 }; tile.Children.Add(plot); gpuTiles.Add((tile, choice, plot, value));
            choice.SelectionChanged += (_, _) => { if (!rebuildingGpu && selectedResource?.Kind == ResourceKind.Gpu) RefreshPerformance(); };
            gpuEngines.Children.Add(tile);
        }
        gpuPanel.Children.Add(gpuEngines);
        var memory = new StackPanel { Spacing = 7 };
        memory.Children.Add(gpuDedicatedLabel); memory.Children.Add(gpuDedicated);
        gpuSharedLabel.Margin = new Thickness(0, 5, 0, 0); memory.Children.Add(gpuSharedLabel); memory.Children.Add(gpuShared);
        Grid.SetRow(memory, 1); gpuPanel.Children.Add(memory);
        return gpuPanel;
    }

    private void RefreshGpu(ResourceRow resource, long now)
    {
        var gpu = resource.Gpu!;
        var engines = gpu.OrderedEngines().ToArray();
        var defaults = engines.GroupBy(engine => engine.Name).Select(group => group.First()).Take(4).ToArray();
        int columns = root.ActualWidth <= 1000 ? 1 : 2;
        string layout = resource.Id + ":" + columns + ":" + string.Join('|', engines.Select(engine => engine.Id));
        if (layout != gpuLayout) {
            gpuLayout = layout;
            rebuildingGpu = true;
            gpuEngines.ColumnDefinitions.Clear(); gpuEngines.RowDefinitions.Clear();
            for (int i = 0; i < columns; ++i) gpuEngines.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < (4 + columns - 1) / columns; ++i) gpuEngines.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < gpuTiles.Count; ++i) {
                var tile = gpuTiles[i];
                tile.Tile.Visibility = i < Math.Max(1, engines.Length) ? Visibility.Visible : Visibility.Collapsed;
                Grid.SetColumn(tile.Tile, i % columns); Grid.SetRow(tile.Tile, i / columns);
                string? selected = (tile.Choice.SelectedItem as ComboBoxItem)?.Tag as string;
                tile.Choice.Items.Clear();
                foreach (var engine in engines) tile.Choice.Items.Add(new ComboBoxItem { Content = engine.Name + (engines.Count(other => other.Name == engine.Name) > 1 ? " · " + (Array.IndexOf(engines, engine) + 1) : ""), Tag = engine.Id });
                if (engines.Length == 0) tile.Choice.Items.Add(new ComboBoxItem { Content = "Engine není dostupný" });
                tile.Choice.IsEnabled = engines.Length > 0;
                tile.Choice.SelectedIndex = selected is not null && engines.Any(engine => engine.Id == selected)
                    ? Array.FindIndex(engines, engine => engine.Id == selected)
                    : defaults.Length > 0 ? Array.IndexOf(engines, defaults[Math.Min(i, defaults.Length - 1)]) : 0;
            }
            rebuildingGpu = false;
        }
        foreach (var tile in gpuTiles) {
            string? id = (tile.Choice.SelectedItem as ComboBoxItem)?.Tag as string;
            var engine = id is not null && gpu.Engines.TryGetValue(id, out var found) ? found : null;
            tile.Value.Text = resource.Stale ? "Zastaralé" : engine is null ? "Nedostupné" : Display.Metric(engine.Current, " %");
            tile.Chart.Set(engine?.History, null, now, rangeSeconds, 100, 0xcc9bb6);
            AutomationProperties.SetName(tile.Chart, "Využití GPU " + (engine?.Name ?? "nedostupné") + ": " + tile.Value.Text);
        }
        string MemoryValue(Metric value, double capacity) => resource.Stale ? "Zastaralé" : Display.Metric(value, "", 1073741824) + " / " + Display.Number(capacity / 1073741824, " GiB");
        gpuDedicatedLabel.Text = "Vyhrazená paměť GPU   " + MemoryValue(resource.Secondary, resource.Capacity);
        gpuSharedLabel.Text = "Sdílená paměť GPU   " + MemoryValue(resource.Tertiary, gpu.SharedCapacity);
        gpuDedicated.Set(gpu.Dedicated, null, now, rangeSeconds, Math.Max(1, resource.Capacity), 0xcc9bb6);
        gpuShared.Set(gpu.Shared, null, now, rangeSeconds, Math.Max(1, gpu.SharedCapacity), 0xabb0f1);
    }

    private void CheckResourceViews()
    {
        var previous = resourceList.SelectedItem;
        foreach (var resource in model.Resources) {
            resourceList.SelectedItem = resource; RefreshPerformance(); root.UpdateLayout();
            bool expected = resource.Kind is ResourceKind.Cpu or ResourceKind.Memory;
            if ((consumers.Visibility == Visibility.Visible) != expected || (!expected && consumers.Children.Count != 0))
                throw new InvalidOperationException("CPU process cards were shown for an unrelated resource.");
            if (resource.Kind != ResourceKind.Gpu) continue;
            if (gpuPanel.Visibility != Visibility.Visible || chart.Visibility != Visibility.Collapsed || gpuDedicated.ActualWidth <= 0 || gpuShared.ActualWidth <= 0)
                throw new InvalidOperationException("GPU graphs did not render within their detail.");
            var gpu = resource.Gpu!;
            if (gpu.Engines.Count > 0) {
                var choice = gpuTiles[0].Choice; choice.SelectedIndex = choice.Items.Count - 1;
                var id = (string)((ComboBoxItem)choice.SelectedItem).Tag;
                if (!gpu.Engines.ContainsKey(id) || gpu.Engines[id].History.Count == 0) throw new InvalidOperationException("GPU engine selection lost its history.");
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
