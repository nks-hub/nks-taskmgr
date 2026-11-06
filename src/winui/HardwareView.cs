using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly ScrollViewer hardwarePage = new() { Visibility = Visibility.Collapsed, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 20, 0) };
    private readonly Grid hardwareCards = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly Dictionary<string, HardwareFactRows> hardwareSections = [];
    private readonly CapabilityPanel capabilities = new();
    private readonly TextBlock hardwareSummary = Label("", 14, 0xe9eef0);
    private readonly Button hardwareCopy = Button(Loc.T("Kopírovat"));
    private Border capabilityCard = null!;

    private void BuildHardware()
    {
        // One line that answers "what machine is this", the facts in compact cards, permissions folded away at the end.
        var top = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 12) };
        top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hardwareSummary.TextWrapping = TextWrapping.Wrap; hardwareSummary.IsTextSelectionEnabled = true; hardwareSummary.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(hardwareSummary, "hardware-summary");
        top.Children.Add(hardwareSummary);
        hardwareCopy.Height = 30; hardwareCopy.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(hardwareCopy, Loc.T("Zkopírovat celou sestavu jako text do schránky"));
        hardwareCopy.Click += (_, _) => CopyHardware();
        Grid.SetColumn(hardwareCopy, 1); top.Children.Add(hardwareCopy);
        hardwareCards.ColumnDefinitions.Add(new ColumnDefinition()); hardwareCards.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 5; ++i) hardwareCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var key in new[] { "cpu", "memory", "board", "ups" }) {
            var body = new StackPanel { Padding = new Thickness(14, 12, 14, 12), Spacing = 8 };
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = Label(key switch { "cpu" => Loc.T("Procesor"), "memory" => Loc.T("Paměť"), "board" => Loc.T("Základní deska a BIOS"), _ => Loc.T("Záložní zdroj UPS") }, 14, 0xe9eef0);
            title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; title.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(title);
            body.Children.Add(heading);
            if (key == "cpu") AddCpuSensorChoice(heading);
            var values = new HardwareFactRows(); body.Children.Add(values.Panel); hardwareSections.Add(key, values);
            if (key == "cpu") AddCpuSensorDetails(body);
            hardwareCards.Children.Add(Card(body));
        }
        var capabilityExpander = new Expander { Header = Loc.T("Oprávnění a dostupnost měření"), Content = capabilities, IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        capabilityCard = Card(capabilityExpander); hardwareCards.Children.Add(capabilityCard);
        var content = new StackPanel(); content.Children.Add(top); content.Children.Add(hardwareCards);
        hardwarePage.Content = content; page.Children.Add(hardwarePage);
    }

    private void ResizeHardware()
    {
        int columns = root.ActualWidth < 1000 ? 1 : 2;
        hardwareCards.ColumnDefinitions[1].Width = columns == 1 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        var shown = hardwareCards.Children.Take(4).Where(card => card.Visibility == Visibility.Visible).ToArray();
        for (int i = 0; i < shown.Length; ++i) {
            Grid.SetRow((FrameworkElement)shown[i], i / columns);
            Grid.SetColumn((FrameworkElement)shown[i], i % columns);
        }
        Grid.SetRow(capabilityCard, (shown.Length + columns - 1) / columns); Grid.SetColumn(capabilityCard, 0); Grid.SetColumnSpan(capabilityCard, columns);
    }

    private Dictionary<string, Dictionary<string, string>> HardwareFacts()
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var key in hardwareSections.Keys) {
            var facts = model.Hardware.TryGetValue(key, out var available) ? new Dictionary<string, string>(available) : [];
            if (key == "memory" && model.Memory.State == SampleState.Valid) facts["Instalováno"] = model.Memory.InstalledBytes > 0 ? Display.Number(model.Memory.InstalledBytes / 1073741824d, " GiB") : "Windows nehlásí";
            if (key == "cpu") facts = new Dictionary<string, string> {
                ["Model"] = model.CpuModel.Length > 0 ? model.CpuModel : "Načítání…",
                ["Logické procesory"] = model.LogicalProcessors > 0 ? model.LogicalProcessors.ToString() : "Načítání…"
            }.Concat(facts.Where(fact => fact.Key != "Model" && fact.Key != "Logické procesory")).ToDictionary(fact => fact.Key, fact => fact.Value);
            result[key] = facts;
        }
        return result;
    }

    private void RefreshHardware()
    {
        capabilities.Refresh(model, servicesModel);
        var all = HardwareFacts();
        foreach (var (key, rows) in hardwareSections) {
            var facts = all[key];
            IEnumerable<KeyValuePair<string, string>> shown = key == "cpu" ? facts.Concat(model.CpuSensorSummaryFacts()) : facts;
            if (key != "cpu" && facts.Count == 0) shown = new Dictionary<string, string> { ["Dostupnost"] = "Údaje zatím nejsou dostupné" };
            rows.Update(shown);
        }
        // A machine without a reported UPS does not need a card saying so.
        var upsCard = hardwareCards.Children[3];
        bool noUps = all["ups"].Count == 0 || all["ups"].Values.Any(value => value.Contains("nehlásí", StringComparison.OrdinalIgnoreCase));
        var upsVisibility = noUps ? Visibility.Collapsed : Visibility.Visible;
        if (upsCard.Visibility != upsVisibility) { upsCard.Visibility = upsVisibility; ResizeHardware(); }
        string Fact(string section, string name) => all[section].TryGetValue(name, out var value) && !value.StartsWith("Načít", StringComparison.Ordinal) && !value.Contains("nehlásí", StringComparison.OrdinalIgnoreCase) && !value.Contains("nedostupn", StringComparison.OrdinalIgnoreCase) ? value : "";
        string bios = Fact("board", "Verze BIOS"), date = Fact("board", "Datum BIOS");
        var parts = new[] {
            Fact("cpu", "Model"),
            string.Join(" ", new[] { Fact("memory", "Instalováno"), Fact("memory", "Typ paměti"), Fact("memory", "Rychlost RAM") }.Where(part => part.Length > 0)),
            Fact("board", "Model desky"),
            bios.Length > 0 ? "BIOS " + bios + (date.Length > 0 ? " (" + date + ")" : "") : ""
        }.Where(part => part.Length > 0);
        string summary = string.Join("  ·  ", parts);
        if (hardwareSummary.Text != summary) hardwareSummary.Text = summary;
        cpuSensorDetails.Visibility = model.CpuSensorsEnabled ? Visibility.Visible : Visibility.Collapsed;
        cpuSensorDetailsRows.Update(model.CpuSensorFacts());
    }

    private void CopyHardware()
    {
        var text = new System.Text.StringBuilder();
        var titles = new Dictionary<string, string> { ["cpu"] = Loc.T("Procesor"), ["memory"] = Loc.T("Paměť"), ["board"] = Loc.T("Základní deska a BIOS"), ["ups"] = Loc.T("Záložní zdroj UPS") };
        foreach (var (key, facts) in HardwareFacts()) {
            text.AppendLine(titles[key]);
            foreach (var (name, value) in facts) text.Append("  ").Append(Loc.T(name)).Append(": ").AppendLine(Loc.T(value));
        }
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage(); package.SetText(text.ToString());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        hardwareCopy.Content = Loc.T("Zkopírováno");
        DispatcherQueue.TryEnqueue(async () => { await Task.Delay(1500); hardwareCopy.Content = Loc.T("Kopírovat"); });
    }

    private sealed class HardwareFactRows
    {
        private readonly record struct Key(string Name, int Occurrence);
        private sealed record Row(Grid Line, TextBlock Value);
        private readonly Dictionary<Key, Row> rows = [];
        internal StackPanel Panel { get; } = new() { Spacing = 6 };

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
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); line.ColumnDefinitions.Add(new ColumnDefinition());
                    var caption = Label(Loc.T(name), 12, 0x95a4ad); caption.TextWrapping = TextWrapping.Wrap; line.Children.Add(caption);
                    var reading = Label(Loc.T(value), 12, 0xe9eef0); reading.TextWrapping = TextWrapping.Wrap;
                    Grid.SetColumn(reading, 1); line.Children.Add(reading);
                    rows.Add(key, row = new Row(line, reading));
                } else if (row.Value.Text != Loc.T(value)) row.Value.Text = Loc.T(value);
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
