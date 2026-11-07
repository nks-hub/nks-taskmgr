using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Dictionary<(uint, uint), ChartView> coreCharts = [];
    private readonly Dictionary<(uint, uint), TextBlock> coreLabels = [];
    private readonly ChartView coreOverlay = new() { Height = 260 };
    private string coreLayout = string.Empty;
    private string factsKey = "";
    private TextBlock? hardwareFact;
    private readonly Dictionary<string, TextBlock> factValues = [];
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush performanceInk = Display.Brush(0xe9eef0), performanceMuted = Display.Brush(0x95a4ad), choiceSelected = Display.Brush(0x28343b), choiceIdle = Display.Brush(0x1d252a);

    private void RefreshPerformance()
    {
        var resource = selectedResource;
        if (resource is null) return;
        bool resourceChanged = performanceScaleResource != resource.Id;
        SelectPerformanceScale(resource);
        if (resourceChanged && root.ActualWidth > 0) ResizePerformanceLayout(root.ActualWidth);
        long now = model.Paused ? Math.Max(model.ProcessTime, model.ResourceTime) : NativeMonitor.MonotonicNowNs;
        uint color = Display.ResourceColor(resource.Kind);
        resourceCount.Text = Loc.F("{0} zařízení", model.Resources.Count);
        resourceTitle.Text = resource.Title;
        resourceDescription.Text = resource.Kind == ResourceKind.Cpu ? Loc.F("{0} logických procesorů", model.LogicalProcessors)
            : resource.Kind == ResourceKind.Memory && model.Memory.State == SampleState.Valid
                ? Loc.F("{0} GiB celkem", Display.Number(model.Memory.PhysicalBytes / 1073741824d, "")) : resource.Description;
        var measurement = model.MeasurementFor(resource, false);
        if (resource.IsVirtualNetwork) resourceDescription.Text += " · " + Loc.T("virtuální síťové rozhraní");
        string context = MeasurementText.Describe(measurement, resource.Primary, resource.Stale);
        if (resource.Kind == ResourceKind.Cpu && cpuMode > 0)
            context += "\n\n" + Loc.T("Grafy logických procesorů:") + "\n" + MeasurementText.DescribeFormat(model.MeasurementFor(resource, true));
        ToolTipService.SetToolTip(resourceDescription, context);
        AutomationProperties.SetHelpText(resourceDescription, context);
        bool valid = !resource.Stale && resource.Primary.State == SampleState.Valid;
        resourceValue.FontSize = valid ? 18 : 12;
        resourceValue.Foreground = Display.Brush(color);
        resourceValue.Text = valid ? Display.Number(resource.Primary.Value / (resource.Kind == ResourceKind.Memory ? 1073741824d : resource.Kind == ResourceKind.Network ? 1_000_000d : 1), "") : resource.Value;
        resourceUnit.Text = valid ? resource.Kind == ResourceKind.Memory ? "GiB" : resource.Kind == ResourceKind.Network ? "Mbit/s ↓" : "%" : "";
        void SelectChoice(ToggleButton button, bool selected) {
            button.IsChecked = selected; button.Background = selected ? choiceSelected : choiceIdle;
            button.Foreground = selected ? performanceInk : performanceMuted;
        }
        for (int i = 0; i < rangeButtons.Count; ++i) SelectChoice(rangeButtons[i], rangeSeconds == (i == 0 ? 60 : 600));
        for (int i = 0; i < cpuButtons.Count; ++i) SelectChoice(cpuButtons[i], cpuMode == i);
        SelectChoice(scaleChoice, scaleChoice.IsChecked == true);
        resourceEyebrow.Text = resource.Kind switch {
            ResourceKind.Cpu => Loc.T("Využití"), ResourceKind.Memory => Loc.T("Použito"), ResourceKind.Disk => Loc.T("Aktivní čas"),
            ResourceKind.Gpu => Loc.T("Využití"), _ => Loc.T("Příjem")
        };
        bool cpu = resource.Kind == ResourceKind.Cpu, memory = resource.Kind == ResourceKind.Memory, gpu = resource.Kind == ResourceKind.Gpu;
        cpuModes.Visibility = cpu ? Visibility.Visible : Visibility.Collapsed;
        processors.Visibility = cpu && cpuMode > 0 ? Visibility.Visible : Visibility.Collapsed;
        chart.Visibility = gpu || (cpu && cpuMode > 0) ? Visibility.Collapsed : Visibility.Visible;
        gpuPanel.Visibility = gpu ? Visibility.Visible : Visibility.Collapsed;
        if (gpu) RefreshGpu(resource, now);
        if (chart.Visibility == Visibility.Visible) {
            chart.Configure(resource.Kind == ResourceKind.Network ? Loc.T("Příjem ↓") : resource.Title,
                memory ? ChartUnit.Bytes : resource.Kind == ResourceKind.Network ? ChartUnit.BitsPerSecond : ChartUnit.Percent, secondName: Loc.T("Odesílání ↑"));
            double limit = resource.Kind == ResourceKind.Network ? scaleChoice.IsChecked == true ? ChartView.ScaleMaximum(resource.Maximum(now, rangeSeconds), double.MaxValue) : Math.Max(resource.Capacity, 1_000_000)
                : resource.Kind == ResourceKind.Memory ? Math.Max(1, resource.Capacity) : 100;
            chart.Set(resource.History, resource.Kind == ResourceKind.Network ? resource.Upload : null, now, rangeSeconds, limit, color,
                autoScale: scaleChoice.IsChecked == true && resource.Kind != ResourceKind.Network);
        }
        diskTransferPanel.Visibility = resource.Kind == ResourceKind.Disk ? Visibility.Visible : Visibility.Collapsed;
        if (resource.Kind == ResourceKind.Disk) RefreshDiskTransfers(resource, now);
        if (cpu && cpuMode > 0) RefreshProcessors(now);
        pageFile.Visibility = pageFileTitle.Visibility = memory ? Visibility.Visible : Visibility.Collapsed;
        if (memory) {
            pageFileTitle.Text = Loc.T("Stránkovací soubor (swap)") + "   " + (resource.Stale ? Loc.T("Zastaralé údaje") : model.Memory.PageFileState == SampleState.Valid
                ? Display.Number(model.Memory.PageFileUsedBytes / 1073741824d, " / ") + Display.Number(model.Memory.PageFileBytes / 1073741824d, " GiB")
                : Display.Metric(new Metric { State = model.Memory.PageFileState }, string.Empty));
            pageFile.Configure(Loc.T("Použitý swap"), ChartUnit.Bytes);
            pageFile.Set(model.PageFile, null, now, rangeSeconds, Math.Max(1, model.Memory.PageFileBytes), 0xdfaf76, autoScale: scaleChoice.IsChecked == true);
        }
        double duration = resource.History.Count == 0 ? 0 : (resource.History.LastTime - resource.History.FirstTime) / 1_000_000_000d;
        retained.Text = Loc.F("Historie {0} · {1} vzorků · zobrazeno {2} s", Display.Number(duration, " s"), resource.History.Count, rangeSeconds);
        string nextFacts = resource.Id + ":" + (memory ? model.Memory.State.ToString() : "") + ":" + string.Join('|', resource.HardwareFacts.Keys);
        if (factsKey != nextFacts) {
            diskDetailsExpanded = diskDetailsResource == resource.Id && diskDetails?.IsExpanded == true;
            factsKey = nextFacts; facts.Children.Clear(); factValues.Clear(); hardwareFact = null; diskDetails = null;
            performanceFacts.Children.Clear(); performanceFactCells.Clear(); facts.Children.Add(performanceFacts);
        }
        void Fact(string name, string value) {
            if (factValues.TryGetValue(name, out var existing)) { if (existing.Text != value) existing.Text = value; return; }
            var block = new StackPanel { Spacing = 1 };
            var caption = Label(Loc.T(name), 10, 0x95a4ad); caption.TextWrapping = TextWrapping.Wrap; block.Children.Add(caption);
            var text = Label(value, 13, 0xe9eef0); text.TextWrapping = TextWrapping.Wrap; block.Children.Add(text);
            performanceFacts.Children.Add(block); performanceFactCells.Add(block); factValues.Add(name, text);
        }
        void Hardware(string value) {
            if (hardwareFact is not null) { if (hardwareFact.Text != value) hardwareFact.Text = value; return; }
            hardwareFact = Label(value, 12, 0x95a4ad); hardwareFact.TextWrapping = TextWrapping.Wrap; facts.Children.Insert(0, hardwareFact);
        }
        if (cpu) {
            Hardware(model.CpuModel);
            Fact(Loc.T("Logické procesory"), model.LogicalProcessors.ToString()); Fact(Loc.T("Procesy"), model.Processes.Count.ToString());
            Fact(Loc.T("Doba sběru"), Display.Number(model.CollectionMs, " ms")); Fact(Loc.T("Vlákna"), model.Processes.Sum(row => (long)row.Threads).ToString());
        } else if (memory && model.Memory.State == SampleState.Valid) {
            Hardware((model.Memory.InstalledBytes > 0 ? Loc.T("Instalováno") : Loc.T("Fyzická paměť")) + " "
                + Display.Number((model.Memory.InstalledBytes > 0 ? model.Memory.InstalledBytes : model.Memory.PhysicalBytes) / 1073741824d, " GiB"));
            Fact(Loc.T("Dostupná paměť"), Display.Number(model.Memory.AvailableBytes / 1073741824d, " GiB"));
            Fact(Loc.T("Potvrzeno / limit"), Display.Number(model.Memory.CommittedBytes / 1073741824d, " / ") + Display.Number(model.Memory.CommitLimitBytes / 1073741824d, " GiB"));
            Fact(Loc.T("Stránkovaný fond"), Display.Number(model.Memory.PagedPoolBytes / 1073741824d, " GiB"));
            Fact(Loc.T("Nestránkovaný fond"), Display.Number(model.Memory.NonpagedPoolBytes / 1073741824d, " GiB"));
            Fact(Loc.T("V mezipaměti"), Display.Number(model.Memory.CacheBytes / 1073741824d, " GiB"));
            Fact(Loc.T("Maximum swapu"), model.Memory.PageFileState == SampleState.Valid ? Display.Number(model.Memory.PageFilePeakBytes / 1073741824d, " GiB")
                : Display.Metric(new Metric { State = model.Memory.PageFileState }, string.Empty));
        } else if (memory) {
            Fact(Loc.T("Dostupnost"), Display.Metric(new Metric { State = model.Memory.State }, string.Empty));
        } else {
            Hardware(resource.Description);
            Fact(Loc.T("Stav"), resource.Status.Length > 0 ? resource.Status : resource.Value);
            if (resource.Kind == ResourceKind.Network) {
                Fact(Loc.T("Příjem"), resource.Stale ? Loc.T("Zastaralé") : Display.Metric(resource.Primary, " Mbit/s", 1_000_000));
                Fact(Loc.T("Odesílání"), resource.Stale ? Loc.T("Zastaralé") : Display.Metric(resource.Secondary, " Mbit/s", 1_000_000));
                NetworkTotals? since = model.NetworkTransfers.TryGetValue(resource.Id, out var own) ? own : null;
                Fact(Loc.T("Přijato od spuštění"), NetworkTotal(since, true, resource.Stale));
                Fact(Loc.T("Odesláno od spuštění"), NetworkTotal(since, false, resource.Stale));
                Fact(Loc.T("Počítáno od"), since is { State: SampleState.Valid, StartedAtUnixNs: > 0 } start
                    ? DateTimeOffset.FromUnixTimeMilliseconds(start.StartedAtUnixNs / 1_000_000).LocalDateTime.ToString(Loc.Language == "cs" ? "d. M. yyyy H:mm:ss" : "G", Loc.Culture) : Loc.T("nedostupné"));
                Fact(Loc.T("Fyzické adaptéry od spuštění"), resource.Stale ? Loc.T("Zastaralé") : model.PhysicalNetworkTotals() is { State: SampleState.Valid } physical
                    ? "↓ " + Display.Bytes(physical.ReceivedBytes) + " · ↑ " + Display.Bytes(physical.SentBytes) : Loc.T("nedostupné"));
            } else if (resource.Kind == ResourceKind.Disk) {
                Fact(Loc.T("Čtení"), resource.Stale ? Loc.T("Zastaralé") : Display.Metric(resource.Secondary, " MiB/s", 1048576)); Fact(Loc.T("Zápis"), resource.Stale ? Loc.T("Zastaralé") : Display.Metric(resource.Tertiary, " MiB/s", 1048576));
                RefreshDiskFacts(resource, Fact);
            } else {
                Fact(Loc.T("Vyhrazená paměť"), Display.Metric(resource.Secondary, " MiB", 1048576)); Fact(Loc.T("Sdílená paměť"), Display.Metric(resource.Tertiary, " MiB", 1048576));
                Fact(Loc.T("Kapacita VRAM"), Display.Number(resource.Capacity / 1073741824, " GiB"));
                Fact(Loc.T("Limit sdílené paměti"), Display.Number(resource.Gpu!.SharedCapacity / 1073741824, " GiB"));
                Fact(Loc.T("Enginy"), resource.Gpu.Engines.Count.ToString());
                Fact(Loc.T("Teplota GPU"), resource.Stale ? Loc.T("Zastaralé") : Display.Metric(resource.Gpu.Temperature, " °C"));
                Fact(Loc.T("Celkové vytížení"), Loc.T("Nejvytíženější engine"));
            }
        }
        foreach (var fact in resource.HardwareFacts) {
            if (cpu && fact.Key is "Rychlost" or "Základní rychlost") Fact(fact.Key, Display.Frequency(fact.Value));
            else if (resource.Kind != ResourceKind.Disk && (!cpu || fact.Key == "Fyzická jádra" || fact.Key == "Patice" || fact.Key.StartsWith("Mezipaměť", StringComparison.Ordinal))) Fact(fact.Key, Loc.Native(fact.Value));
        }
        if (memory && model.Memory.State == SampleState.Valid && model.Memory.InstalledBytes >= model.Memory.PhysicalBytes)
            Fact(Loc.T("Rezervováno hardwarem"), Display.Number((model.Memory.InstalledBytes - model.Memory.PhysicalBytes) / 1048576d, " MiB"));
        ArrangePerformanceFacts();
        RefreshConsumers(cpu, memory, color);
    }

    private static string NetworkTotal(NetworkTotals? value, bool received, bool stale) => stale ? Loc.T("Zastaralé")
        : value is { State: SampleState.Valid } total ? Display.Bytes(received ? total.ReceivedBytes : total.SentBytes) : Loc.T("nedostupné");

    private void RefreshProcessors(long now)
    {
        var keys = model.Processors.Keys.OrderBy(key => key.Group).ThenBy(key => key.Index).ToArray();
        double plotWidth = Math.Max(1, performanceDetailWidth);
        int preferred = keys.Length <= 4 ? 2 : keys.Length <= 8 ? 4 : 6;
        int columns = Math.Max(1, Math.Min(Math.Min(preferred, keys.Length), (int)(plotWidth / 112)));
        int rows = Math.Max(1, (keys.Length + columns - 1) / columns);
        double coreHeight = Math.Clamp(performancePlotHeight / rows - 20, 52, 220);
        string layout = cpuMode + ":" + columns + ":" + string.Join(',', keys.Select(key => key.Group + ":" + key.Index));
        if (layout != coreLayout) {
            coreLayout = layout; processors.Children.Clear(); processors.ColumnDefinitions.Clear(); processors.RowDefinitions.Clear(); coreCharts.Clear(); coreLabels.Clear();
            for (int i = 0; i < (cpuMode == 1 ? columns : 1); ++i) processors.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < (cpuMode == 1 ? (keys.Length + columns - 1) / columns : 1); ++i)
                processors.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            processors.ColumnSpacing = processors.RowSpacing = cpuMode == 1 ? 6 : 0;
            if (cpuMode == 2) processors.Children.Add(coreOverlay);
            for (int i = 0; i < keys.Length; ++i) {
                if (cpuMode == 2) break;
                var core = new ChartView { Height = coreHeight, Timeline = performanceTimeline };
                core.Configure(Loc.F("Logický procesor {0}:{1}", keys[i].Group, keys[i].Index), showAxes: false, interactive: cpuMode == 1);
                AutomationProperties.SetName(core, Loc.F("Logický procesor {0}:{1}", keys[i].Group, keys[i].Index));
                coreCharts.Add(keys[i], core);
                FrameworkElement child = core;
                if (cpuMode == 1) {
                    var tile = new StackPanel { Spacing = 3, Padding = new Thickness(4) };
                    var label = Label("", 9, 0x95a4ad); coreLabels.Add(keys[i], label);
                    tile.Children.Add(label); tile.Children.Add(core); child = tile;
                    Grid.SetColumn(child, i % columns); Grid.SetRow(child, i / columns);
                }
                processors.Children.Add(child);
            }
        }
        if (cpuMode == 2 && keys.Length == 0) {
            coreOverlay.SetOverlays([]);
            coreOverlay.Configure(Loc.T("Logické procesory · měření nedostupné"));
            coreOverlay.Set(null, null, now, rangeSeconds, 100, 0x80c5c0);
            return;
        }
        if (cpuMode == 2 && keys.Length > 0) {
            uint[] colors = [0x80c5c0, 0xabb0f1, 0x83bca0, 0xcc9bb6, 0xdfaf76];
            coreOverlay.Configure($"CPU {keys[0].Group}:{keys[0].Index}");
            coreOverlay.SetOverlays(keys.Skip(1).Select((key, index) => ($"CPU {key.Group}:{key.Index}", model.Processors[key], colors[(index + 1) % colors.Length])).ToArray());
            coreOverlay.Set(model.Processors[keys[0]], null, now, rangeSeconds, 100, colors[0], area: false, autoScale: scaleChoice.IsChecked == true);
            return;
        }
        double coreMaximum = scaleChoice.IsChecked == true ? ChartView.ScaleMaximum(keys.Select(key => model.Processors[key].Maximum(now - rangeSeconds * 1_000_000_000L, 1)).DefaultIfEmpty(1).Max(), 100) : 100;
        foreach (var key in keys) {
            coreCharts[key].Height = coreHeight;
            coreLabels[key].Text = $"{key.Group}:{key.Index} · 0–" + Display.Number(coreMaximum, " %", 0);
            coreCharts[key].Set(model.Processors[key], null, now, rangeSeconds, coreMaximum, 0x80c5c0);
        }
    }
}
