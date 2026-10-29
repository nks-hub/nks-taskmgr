using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Dictionary<(uint, uint), ChartView> coreCharts = [];
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
        bool valid = !resource.Stale && resource.Primary.State == SampleState.Valid;
        resourceValue.FontSize = valid ? 42 : 20;
        resourceValue.Foreground = performanceInk;
        resourceValue.Text = valid ? Display.Number(resource.Primary.Value / (resource.Kind == ResourceKind.Memory ? 1073741824d : resource.Kind == ResourceKind.Network ? 1_000_000d : 1), "") : resource.Value;
        resourceUnit.Text = valid ? resource.Kind == ResourceKind.Memory ? "GiB" : resource.Kind == ResourceKind.Network ? "Mbit/s ↓" : "%" : "";
        void SelectChoice(ToggleButton button, bool selected) {
            button.IsChecked = selected; button.Background = selected ? choiceSelected : choiceIdle;
            button.Foreground = selected ? performanceInk : performanceMuted;
        }
        for (int i = 0; i < rangeButtons.Count; ++i) SelectChoice(rangeButtons[i], rangeSeconds == (i == 0 ? 60 : 600));
        for (int i = 0; i < cpuButtons.Count; ++i) SelectChoice(cpuButtons[i], cpuMode == i);
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
        if (chart.Visibility == Visibility.Visible) chart.Set(resource.History, resource.Kind == ResourceKind.Network ? resource.Upload : null, now, rangeSeconds, resource.Maximum(now, rangeSeconds), color);
        if (cpu && cpuMode > 0) RefreshProcessors(now);
        pageFile.Visibility = pageFileTitle.Visibility = memory ? Visibility.Visible : Visibility.Collapsed;
        if (memory) {
            pageFileTitle.Text = "Stránkovací soubor (swap)   " + (model.Memory.PageFileState == SampleState.Valid
                ? Display.Number(model.Memory.PageFileUsedBytes / 1073741824d, " / ") + Display.Number(model.Memory.PageFileBytes / 1073741824d, " GiB")
                : Display.Metric(new Metric { State = model.Memory.PageFileState }, string.Empty));
            pageFile.Set(model.PageFile, null, now, rangeSeconds, Math.Max(1, model.Memory.PageFileBytes), 0xdfaf76);
        }
        double duration = resource.History.Count == 0 ? 0 : Math.Min(rangeSeconds, (resource.History.LastTime - resource.History.FirstTime) / 1_000_000_000d);
        retained.Text = Display.Number(duration, " s uchováno") + " · " + resource.History.Count + " vzorků · okno " + rangeSeconds + " s";
        string nextFacts = resource.Id + ":" + (memory ? model.Memory.State.ToString() : "");
        if (factsKey != nextFacts) {
            factsKey = nextFacts; facts.Children.Clear(); factValues.Clear(); hardwareFact = null;
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
            } else {
                Fact("Vyhrazená paměť", Display.Metric(resource.Secondary, " MiB", 1048576)); Fact("Sdílená paměť", Display.Metric(resource.Tertiary, " MiB", 1048576));
                Fact("Kapacita VRAM", Display.Number(resource.Capacity / 1073741824, " GiB"));
                Fact("Limit sdílené paměti", Display.Number(resource.Gpu!.SharedCapacity / 1073741824, " GiB"));
                Fact("Enginy", resource.Gpu.Engines.Count.ToString());
                Fact("Teplota GPU", resource.Stale ? "Zastaralé" : Display.Metric(resource.Gpu.Temperature, " °C"));
                Fact("Celkové vytížení", "Nejvytíženější engine");
            }
        }
        consumers.Visibility = cpu || memory ? Visibility.Visible : Visibility.Collapsed;
        consumers.Children.Clear();
        if (!cpu && !memory) return;
        consumers.Children.Add(Label("SOUVISLOSTI", 9, 0x95a4ad));
        consumers.Children.Add(Label(memory ? "Procesy podle pracovní sady" : "Procesy podle CPU", 15, 0xe9eef0));
        var ranked = model.TopConsumers(memory);
        var cards = new Grid { ColumnSpacing = 14 };
        int count = 0;
        bool stacked = root.ActualWidth <= 1000;
        foreach (var row in ranked) {
            int index = count++;
            if (stacked) cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); else cards.ColumnDefinitions.Add(new ColumnDefinition());
            var content = new Grid { ColumnSpacing = 10 };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) content.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            var order = Label(count.ToString("00"), 9, 0x95a4ad); order.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(order);
            var icon = new Border { Child = UiIcon.Create(false, color, 18), Background = Display.Brush(0x28343b), Width = 30, Height = 30, CornerRadius = new CornerRadius(7) };
            Grid.SetColumn(icon, 1); content.Children.Add(icon);
            var name = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            var title = Label(row.Name, 11, 0xe9eef0); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; name.Children.Add(title);
            name.Children.Add(Label("PID " + row.PidText, 9, 0x95a4ad)); Grid.SetColumn(name, 2); content.Children.Add(name);
            var value = Label(memory ? row.WorkingText : row.CpuText, 12, 0xe9eef0); value.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(value, 3); content.Children.Add(value);
            var button = Button(""); button.Content = content; button.Height = 74; button.Padding = new Thickness(17, 12, 17, 12);
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Margin = new Thickness(0, 0, 0, stacked ? 8 : 0);
            button.Click += (_, _) => {
                if (!model.IsCurrent(row)) return;
                search.Text = string.Empty; model.SetMode(ProcessMode.All); ShowView(false);
                processList.SelectedItems.Clear(); processList.SelectedItem = row; processList.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
            };
            if (stacked) Grid.SetRow(button, index); else Grid.SetColumn(button, index); cards.Children.Add(button);
        }
        consumers.Children.Add(cards);
    }

    private void RefreshProcessors(long now)
    {
        var keys = model.Processors.Keys.OrderBy(key => key.Group).ThenBy(key => key.Index).ToArray();
        int columns = root.ActualWidth <= 1000 ? 3 : 6;
        string layout = cpuMode + ":" + columns + ":" + string.Join(',', keys.Select(key => key.Group + ":" + key.Index));
        if (layout != coreLayout) {
            coreLayout = layout; processors.Children.Clear(); processors.ColumnDefinitions.Clear(); processors.RowDefinitions.Clear(); coreCharts.Clear();
            for (int i = 0; i < (cpuMode == 1 ? columns : 1); ++i) processors.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < (cpuMode == 1 ? (keys.Length + columns - 1) / columns : 1); ++i)
                processors.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            processors.ColumnSpacing = processors.RowSpacing = cpuMode == 1 ? 6 : 0;
            for (int i = 0; i < keys.Length; ++i) {
                var core = new ChartView { Height = cpuMode == 1 ? 52 : 236 };
                AutomationProperties.SetName(core, $"Logický procesor {keys[i].Group}:{keys[i].Index}");
                coreCharts.Add(keys[i], core);
                FrameworkElement child = core;
                if (cpuMode == 1) {
                    var tile = new StackPanel { Spacing = 3, Padding = new Thickness(4) };
                    tile.Children.Add(Label($"{keys[i].Group}:{keys[i].Index}", 9, 0x95a4ad)); tile.Children.Add(core); child = tile;
                    Grid.SetColumn(child, i % columns); Grid.SetRow(child, i / columns);
                }
                processors.Children.Add(child);
            }
        }
        foreach (var key in keys) coreCharts[key].Set(model.Processors[key], null, now, rangeSeconds, 100,
            cpuMode == 1 ? 0x80c5c0u : new uint[] { 0x80c5c0, 0xabb0f1, 0x83bca0, 0xcc9bb6, 0xdfaf76 }[(key.Group + key.Index) % 5], cpuMode == 1, cpuMode == 1);
    }
}
