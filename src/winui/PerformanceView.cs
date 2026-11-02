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
        long now = model.Paused ? Math.Max(model.ProcessTime, model.ResourceTime) : NativeMonitor.MonotonicNowNs;
        uint color = Display.ResourceColor(resource.Kind);
        resourceCount.Text = model.Resources.Count + " zařízení";
        resourceTitle.Text = resource.Title;
        resourceDescription.Text = resource.Kind == ResourceKind.Cpu ? model.LogicalProcessors + " logických procesorů"
            : resource.Kind == ResourceKind.Memory && model.Memory.State == SampleState.Valid
                ? Display.Number(model.Memory.PhysicalBytes / 1073741824d, " GiB celkem") : resource.Description;
        var measurement = model.MeasurementFor(resource, false);
        resourceDescription.Text += " · " + MeasurementText.Summary(measurement);
        if (resource.IsVirtualNetwork) resourceDescription.Text += " · virtuální síťové rozhraní";
        string context = MeasurementText.Describe(measurement, resource.Primary, resource.Stale);
        if (resource.Kind == ResourceKind.Cpu && cpuMode > 0)
            context += "\n\nGrafy logických procesorů:\n" + MeasurementText.DescribeFormat(model.MeasurementFor(resource, true));
        ToolTipService.SetToolTip(resourceDescription, context);
        AutomationProperties.SetHelpText(resourceDescription, context);
        bool valid = !resource.Stale && resource.Primary.State == SampleState.Valid;
        resourceValue.FontSize = valid ? 24 : 13;
        resourceValue.Foreground = performanceInk;
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
            ResourceKind.Cpu => "VYUŽITÍ PROCESORU", ResourceKind.Memory => "FYZICKÁ PAMĚŤ", ResourceKind.Disk => "AKTIVITA DISKU",
            ResourceKind.Gpu => "GRAFICKÝ ADAPTÉR", _ => "SÍŤOVÝ ADAPTÉR"
        };
        bool cpu = resource.Kind == ResourceKind.Cpu, memory = resource.Kind == ResourceKind.Memory, gpu = resource.Kind == ResourceKind.Gpu;
        cpuModes.Visibility = cpu ? Visibility.Visible : Visibility.Collapsed;
        processors.Visibility = cpu && cpuMode > 0 ? Visibility.Visible : Visibility.Collapsed;
        chart.Visibility = gpu || (cpu && cpuMode > 0) ? Visibility.Collapsed : Visibility.Visible;
        gpuPanel.Visibility = gpu ? Visibility.Visible : Visibility.Collapsed;
        if (gpu) RefreshGpu(resource, now);
        if (chart.Visibility == Visibility.Visible) {
            chart.Configure(resource.Kind == ResourceKind.Network ? "Příjem ↓" : resource.Title,
                memory ? ChartUnit.Bytes : resource.Kind == ResourceKind.Network ? ChartUnit.BitsPerSecond : ChartUnit.Percent, secondName: "Odesílání ↑");
            double limit = resource.Kind == ResourceKind.Network ? scaleChoice.IsChecked == true ? ChartView.ScaleMaximum(resource.Maximum(now, rangeSeconds), Math.Max(resource.Capacity, 1_000_000)) : Math.Max(resource.Capacity, 1_000_000)
                : resource.Kind == ResourceKind.Memory ? Math.Max(1, resource.Capacity) : 100;
            chart.Set(resource.History, resource.Kind == ResourceKind.Network ? resource.Upload : null, now, rangeSeconds, limit, color,
                autoScale: scaleChoice.IsChecked == true && resource.Kind != ResourceKind.Network);
        }
        if (cpu && cpuMode > 0) RefreshProcessors(now);
        pageFile.Visibility = pageFileTitle.Visibility = memory ? Visibility.Visible : Visibility.Collapsed;
        if (memory) {
            pageFileTitle.Text = "Stránkovací soubor (swap)   " + (model.Memory.PageFileState == SampleState.Valid
                ? Display.Number(model.Memory.PageFileUsedBytes / 1073741824d, " / ") + Display.Number(model.Memory.PageFileBytes / 1073741824d, " GiB")
                : Display.Metric(new Metric { State = model.Memory.PageFileState }, string.Empty));
            pageFile.Configure("Použitý swap", ChartUnit.Bytes);
            pageFile.Set(model.PageFile, null, now, rangeSeconds, Math.Max(1, model.Memory.PageFileBytes), 0xdfaf76, autoScale: scaleChoice.IsChecked == true);
        }
        double duration = resource.History.Count == 0 ? 0 : (resource.History.LastTime - resource.History.FirstTime) / 1_000_000_000d;
        retained.Text = "Historie " + Display.Number(duration, " s") + " · " + resource.History.Count + " vzorků · zobrazeno " + rangeSeconds + " s";
        string nextFacts = resource.Id + ":" + (memory ? model.Memory.State.ToString() : "") + ":" + string.Join('|', resource.HardwareFacts.Keys);
        if (factsKey != nextFacts) {
            diskDetailsExpanded = diskDetailsResource == resource.Id && diskDetails?.IsExpanded == true;
            factsKey = nextFacts; facts.Children.Clear(); factValues.Clear(); hardwareFact = null; diskDetails = null;
            var factsHeading = Label("SESTAVA", 9, 0x95a4ad); factsHeading.CharacterSpacing = 178; facts.Children.Add(factsHeading);
        }
        void Fact(string name, string value) {
            if (factValues.TryGetValue(name, out var existing)) { if (existing.Text != value) existing.Text = value; return; }
            var block = new Grid { ColumnSpacing = 12 };
            block.ColumnDefinitions.Add(new ColumnDefinition()); block.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var caption = Label(name, 10, 0x95a4ad); caption.TextWrapping = TextWrapping.Wrap; block.Children.Add(caption);
            var text = Label(value, 10, 0xe9eef0); text.TextAlignment = TextAlignment.Right; Grid.SetColumn(text, 1); block.Children.Add(text); facts.Children.Add(block); factValues.Add(name, text);
        }
        void Hardware(string value) {
            if (hardwareFact is not null) { if (hardwareFact.Text != value) hardwareFact.Text = value; return; }
            hardwareFact = Label(value, 17, 0xe9eef0); hardwareFact.TextWrapping = TextWrapping.Wrap; hardwareFact.Margin = new Thickness(0, 0, 0, 7); facts.Children.Add(hardwareFact);
        }
        if (cpu) {
            Hardware(model.CpuModel);
            Fact("Logické procesory", model.LogicalProcessors.ToString()); Fact("Procesy", model.Processes.Count.ToString());
            Fact("Doba sběru", Display.Number(model.CollectionMs, " ms")); Fact("Vlákna", model.Processes.Sum(row => (long)row.Threads).ToString());
        } else if (memory && model.Memory.State == SampleState.Valid) {
            Hardware(Display.Number((model.Memory.InstalledBytes > 0 ? model.Memory.InstalledBytes : model.Memory.PhysicalBytes) / 1073741824d, " GiB"));
            Fact("Dostupná paměť", Display.Number(model.Memory.AvailableBytes / 1073741824d, " GiB"));
            Fact("Potvrzeno / limit", Display.Number(model.Memory.CommittedBytes / 1073741824d, " / ") + Display.Number(model.Memory.CommitLimitBytes / 1073741824d, " GiB"));
            Fact("Stránkovaný fond", Display.Number(model.Memory.PagedPoolBytes / 1073741824d, " GiB"));
            Fact("Nestránkovaný fond", Display.Number(model.Memory.NonpagedPoolBytes / 1073741824d, " GiB"));
            Fact("V mezipaměti", Display.Number(model.Memory.CacheBytes / 1073741824d, " GiB"));
            Fact("Maximum swapu", model.Memory.PageFileState == SampleState.Valid ? Display.Number(model.Memory.PageFilePeakBytes / 1073741824d, " GiB")
                : Display.Metric(new Metric { State = model.Memory.PageFileState }, string.Empty));
        } else if (memory) {
            Fact("Dostupnost", Display.Metric(new Metric { State = model.Memory.State }, string.Empty));
        } else {
            Hardware(resource.Description);
            Fact("Stav", resource.Status.Length > 0 ? resource.Status : resource.Value);
            if (resource.Kind == ResourceKind.Network) {
                Fact("Příjem", Display.Metric(resource.Primary, " Mbit/s", 1_000_000));
                Fact("Odesílání", Display.Metric(resource.Secondary, " Mbit/s", 1_000_000));
            } else if (resource.Kind == ResourceKind.Disk) {
                Fact("Čtení", Display.Metric(resource.Secondary, " MiB/s", 1048576)); Fact("Zápis", Display.Metric(resource.Tertiary, " MiB/s", 1048576));
                RefreshDiskFacts(resource, Fact);
            } else {
                Fact("Vyhrazená paměť", Display.Metric(resource.Secondary, " MiB", 1048576)); Fact("Sdílená paměť", Display.Metric(resource.Tertiary, " MiB", 1048576));
                Fact("Kapacita VRAM", Display.Number(resource.Capacity / 1073741824, " GiB"));
                Fact("Limit sdílené paměti", Display.Number(resource.Gpu!.SharedCapacity / 1073741824, " GiB"));
                Fact("Enginy", resource.Gpu.Engines.Count.ToString());
                Fact("Teplota GPU", resource.Stale ? "Zastaralé" : Display.Metric(resource.Gpu.Temperature, " °C"));
                Fact("Celkové vytížení", "Nejvytíženější engine");
            }
        }
        foreach (var fact in resource.HardwareFacts) {
            if (resource.Kind != ResourceKind.Disk && (!cpu || fact.Key == "Fyzická jádra" || fact.Key == "Patice" || fact.Key.StartsWith("Mezipaměť", StringComparison.Ordinal))) Fact(fact.Key, fact.Value);
        }
        if (memory && model.Memory.State == SampleState.Valid && model.Memory.InstalledBytes >= model.Memory.PhysicalBytes)
            Fact("Rezervováno hardwarem", Display.Number((model.Memory.InstalledBytes - model.Memory.PhysicalBytes) / 1048576d, " MiB"));
        RefreshConsumers(cpu, memory, color);
    }

    private void RefreshProcessors(long now)
    {
        var keys = model.Processors.Keys.OrderBy(key => key.Group).ThenBy(key => key.Index).ToArray();
        double plotWidth = Math.Max(1, ((FrameworkElement)chartGrid.Children[0]).ActualWidth - 44);
        int preferred = keys.Length <= 4 ? 2 : keys.Length <= 8 ? 4 : 6;
        int columns = Math.Max(1, Math.Min(Math.Min(preferred, keys.Length), (int)(plotWidth / 112)));
        int rows = Math.Max(1, (keys.Length + columns - 1) / columns);
        double coreHeight = Math.Clamp(260d / rows - 20, 52, 220);
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
                core.Configure($"Logický procesor {keys[i].Group}:{keys[i].Index}", showAxes: false, interactive: cpuMode == 1);
                AutomationProperties.SetName(core, $"Logický procesor {keys[i].Group}:{keys[i].Index}");
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
            coreOverlay.Configure("Logické procesory · měření nedostupné");
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
            coreLabels[key].Text = $"{key.Group}:{key.Index} · 0–" + Display.Number(coreMaximum, " %", 0);
            coreCharts[key].Set(model.Processors[key], null, now, rangeSeconds, coreMaximum, 0x80c5c0);
        }
    }
}
