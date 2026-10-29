using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Dictionary<(uint, uint), ChartView> coreCharts = [];
    private string coreLayout = string.Empty;

    private void RefreshPerformance()
    {
        var resource = selectedResource;
        if (resource is null) return;
        long now = model.Paused ? Math.Max(model.ProcessTime, model.ResourceTime) : NativeMonitor.MonotonicNowNs;
        uint color = Display.ResourceColor(resource.Kind);
        resourceTitle.Text = resource.Title; resourceDescription.Text = resource.Description;
        resourceValue.Text = resource.Value; resourceValue.Foreground = Display.Brush(color);
        resourceEyebrow.Text = resource.Kind switch {
            ResourceKind.Cpu => "VYUŽITÍ PROCESORU", ResourceKind.Memory => "FYZICKÁ PAMĚŤ", ResourceKind.Disk => "AKTIVITA DISKU",
            ResourceKind.Gpu => "GRAFICKÝ ADAPTÉR", _ => "SÍŤOVÝ ADAPTÉR"
        };
        bool cpu = resource.Kind == ResourceKind.Cpu, memory = resource.Kind == ResourceKind.Memory;
        cpuModes.Visibility = cpu ? Visibility.Visible : Visibility.Collapsed;
        processors.Visibility = cpu && cpuMode > 0 ? Visibility.Visible : Visibility.Collapsed;
        chart.Visibility = cpu && cpuMode > 0 ? Visibility.Collapsed : Visibility.Visible;
        chart.Set(resource.History, resource.Kind == ResourceKind.Network ? resource.Upload : null, now, rangeSeconds, resource.Maximum(now, rangeSeconds), color);
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
        facts.Children.Clear(); facts.Children.Add(Label("SESTAVA", 9, 0x95a4ad));
        void Fact(string name, string value) {
            var block = new StackPanel { Spacing = 6 };
            block.Children.Add(Label(name, 9, 0x95a4ad)); block.Children.Add(Label(value, 12, 0xe9eef0)); facts.Children.Add(block);
        }
        if (cpu) {
            facts.Children.Add(Label(model.CpuModel, 14, 0xe9eef0));
            Fact("Logické procesory", model.LogicalProcessors.ToString()); Fact("Procesy", model.Processes.Count.ToString());
            Fact("Doba sběru", Display.Number(model.CollectionMs, " ms")); Fact("Vlákna", model.Processes.Sum(row => (long)row.Threads).ToString());
        } else if (memory && model.Memory.State == SampleState.Valid) {
            facts.Children.Add(Label(Display.Number((model.Memory.InstalledBytes > 0 ? model.Memory.InstalledBytes : model.Memory.PhysicalBytes) / 1073741824d, " GiB"), 16, 0xe9eef0));
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
            facts.Children.Add(Label(resource.Description, 14, 0xe9eef0));
            Fact("Stav", resource.Status.Length > 0 ? resource.Status : resource.Value);
            if (resource.Kind == ResourceKind.Network) {
                Fact("Příjem", Display.Metric(resource.Primary, " Mbit/s", 1_000_000));
                Fact("Odesílání", Display.Metric(resource.Secondary, " Mbit/s", 1_000_000));
            } else if (resource.Kind == ResourceKind.Disk) {
                Fact("Čtení", Display.Metric(resource.Secondary, " MiB/s", 1048576)); Fact("Zápis", Display.Metric(resource.Tertiary, " MiB/s", 1048576));
            } else {
                Fact("Vyhrazená paměť", Display.Metric(resource.Secondary, " MiB", 1048576)); Fact("Sdílená paměť", Display.Metric(resource.Tertiary, " MiB", 1048576));
            }
        }
        consumers.Children.Clear(); consumers.Children.Add(Label("SOUVISLOSTI", 9, 0x95a4ad));
        consumers.Children.Add(Label(memory ? "Procesy podle pracovní sady" : "Procesy podle CPU", 15, 0xe9eef0));
        var ranked = memory ? model.Processes.OrderByDescending(row => row.WorkingBytes) : model.Processes.Where(row => row.Cpu.State == SampleState.Valid).OrderByDescending(row => row.Cpu.Value);
        var cards = new Grid { ColumnSpacing = 14 };
        foreach (var row in ranked.Take(3)) {
            int column = cards.ColumnDefinitions.Count; cards.ColumnDefinitions.Add(new ColumnDefinition());
            var content = new StackPanel { Spacing = 9 };
            content.Children.Add(Label(row.Name, 11, 0xe9eef0)); content.Children.Add(Label("PID " + row.PidText + " · " + (memory ? row.WorkingText : row.CpuText), 10, color));
            var button = Button(""); button.Content = content; button.Height = 74; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Click += (_, _) => {
                if (!model.IsCurrent(row)) return;
                search.Text = string.Empty; model.SetMode(ProcessMode.All); ShowView(false);
                processList.SelectedItems.Clear(); processList.SelectedItem = row; processList.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
            };
            Grid.SetColumn(button, column); cards.Children.Add(button);
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
