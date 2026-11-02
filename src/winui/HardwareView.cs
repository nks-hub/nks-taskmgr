using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly ScrollViewer hardwarePage = new() { Visibility = Visibility.Collapsed, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Grid hardwareCards = new() { ColumnSpacing = 16, RowSpacing = 16 };
    private readonly Dictionary<string, HardwareFactRows> hardwareSections = [];
    private readonly CapabilityPanel capabilities = new();
    private Border capabilityCard = null!;

    private void BuildHardware()
    {
        hardwareCards.ColumnDefinitions.Add(new ColumnDefinition()); hardwareCards.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 5; ++i) hardwareCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var key in new[] { "cpu", "memory", "board", "ups" }) {
            var body = new StackPanel { Padding = new Thickness(22), Spacing = 13 };
            var title = Label(key switch { "cpu" => "Procesor", "memory" => "Paměť", "board" => "Základní deska a BIOS", _ => "Záložní zdroj UPS" }, 18, 0xe9eef0);
            title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; body.Children.Add(title);
            if (key == "cpu") AddCpuSensorChoice(body);
            var values = new HardwareFactRows(); body.Children.Add(values.Panel); hardwareSections.Add(key, values);
            if (key == "cpu") AddCpuSensorDetails(body);
            hardwareCards.Children.Add(Card(body));
        }
        var capabilityBody = new StackPanel { Padding = new Thickness(22), Spacing = 13 };
        capabilityBody.Children.Add(Label("Možnosti a oprávnění", 18, 0xe9eef0)); capabilityBody.Children.Add(capabilities);
        capabilityCard = Card(capabilityBody); hardwareCards.Children.Add(capabilityCard);
        hardwarePage.Content = hardwareCards; page.Children.Add(hardwarePage);
    }

    private void ResizeHardware()
    {
        int columns = root.ActualWidth < 1000 ? 1 : 2;
        hardwareCards.ColumnDefinitions[1].Width = columns == 1 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        for (int i = 0; i < 4; ++i) {
            Grid.SetRow((FrameworkElement)hardwareCards.Children[i], i / columns);
            Grid.SetColumn((FrameworkElement)hardwareCards.Children[i], i % columns);
        }
        Grid.SetRow(capabilityCard, (4 + columns - 1) / columns); Grid.SetColumn(capabilityCard, 0); Grid.SetColumnSpan(capabilityCard, columns);
    }

    private void RefreshHardware()
    {
        capabilities.Refresh(model, servicesModel);
        foreach (var (key, rows) in hardwareSections) {
            var facts = model.Hardware.TryGetValue(key, out var available) ? new Dictionary<string, string>(available) : [];
            if (key == "memory" && model.Memory.State == SampleState.Valid) facts["Instalováno"] = model.Memory.InstalledBytes > 0 ? Display.Number(model.Memory.InstalledBytes / 1073741824d, " GiB") : "Windows nehlásí";
            IEnumerable<KeyValuePair<string, string>> shown = facts;
            if (key == "cpu") shown = new Dictionary<string, string> {
                ["Model"] = model.CpuModel.Length > 0 ? model.CpuModel : "Načítání…",
                ["Logické procesory"] = model.LogicalProcessors > 0 ? model.LogicalProcessors.ToString() : "Načítání…"
            }.Concat(facts).Concat(model.CpuSensorSummaryFacts());
            else if (facts.Count == 0) facts["Dostupnost"] = "Údaje zatím nejsou dostupné";
            rows.Update(shown);
        }
        cpuSensorDetailsRows.Update(model.CpuSensorFacts());
    }

    private sealed class HardwareFactRows
    {
        private readonly record struct Key(string Name, int Occurrence);
        private sealed record Row(Grid Line, TextBlock Value);
        private readonly Dictionary<Key, Row> rows = [];
        internal StackPanel Panel { get; } = new() { Spacing = 12 };

        internal void Update(IEnumerable<KeyValuePair<string, string>> facts)
        {
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            var seen = new HashSet<Key>();
            var ordered = new List<Row>();
            foreach (var (name, value) in facts) {
                int occurrence = occurrences.GetValueOrDefault(name);
                occurrences[name] = occurrence + 1;
                var key = new Key(name, occurrence); seen.Add(key);
                if (!rows.TryGetValue(key, out var row)) {
                    var line = new Grid { ColumnSpacing = 20 };
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) }); line.ColumnDefinitions.Add(new ColumnDefinition());
                    var caption = Label(name, 12, 0x95a4ad); caption.TextWrapping = TextWrapping.Wrap; line.Children.Add(caption);
                    var reading = Label(value, 13, 0xe9eef0); reading.TextWrapping = TextWrapping.Wrap;
                    Grid.SetColumn(reading, 1); line.Children.Add(reading);
                    rows.Add(key, row = new Row(line, reading));
                } else if (row.Value.Text != value) row.Value.Text = value;
                ordered.Add(row);
            }
            foreach (var key in rows.Keys.Where(key => !seen.Contains(key)).ToArray()) {
                Panel.Children.Remove(rows[key].Line); rows.Remove(key);
            }
            for (int i = 0; i < ordered.Count; ++i) {
                var line = ordered[i].Line;
                if (i < Panel.Children.Count && ReferenceEquals(Panel.Children[i], line)) continue;
                int old = Panel.Children.IndexOf(line);
                if (old >= 0) Panel.Children.RemoveAt(old);
                Panel.Children.Insert(i, line);
            }
        }
    }
}
