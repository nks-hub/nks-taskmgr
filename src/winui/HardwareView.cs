using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly ScrollViewer hardwarePage = new() { Visibility = Visibility.Collapsed, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Grid hardwareCards = new() { ColumnSpacing = 16, RowSpacing = 16 };
    private readonly Dictionary<string, StackPanel> hardwareSections = [];
    private readonly Dictionary<string, string> hardwareSignatures = [];
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
            var values = new StackPanel { Spacing = 12 }; body.Children.Add(values); hardwareSections.Add(key, values);
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
        foreach (var (key, panel) in hardwareSections) {
            var facts = model.Hardware.TryGetValue(key, out var available) ? new Dictionary<string, string>(available) : [];
            if (key == "cpu") facts = new Dictionary<string, string> { ["Model"] = model.CpuModel.Length > 0 ? model.CpuModel : "Načítání…", ["Logické procesory"] = model.LogicalProcessors > 0 ? model.LogicalProcessors.ToString() : "Načítání…" }.Concat(facts).Concat(model.CpuSensorFacts()).ToDictionary(pair => pair.Key, pair => pair.Value);
            if (key == "memory" && model.Memory.State == SampleState.Valid) facts["Instalováno"] = model.Memory.InstalledBytes > 0 ? Display.Number(model.Memory.InstalledBytes / 1073741824d, " GiB") : "Windows nehlásí";
            if (facts.Count == 0) facts["Dostupnost"] = "Údaje zatím nejsou dostupné";
            string signature = string.Join('\n', facts.Select(pair => pair.Key + ":" + pair.Value));
            if (hardwareSignatures.GetValueOrDefault(key) == signature) continue;
            hardwareSignatures[key] = signature; panel.Children.Clear();
            foreach (var (name, value) in facts) {
                var line = new Grid { ColumnSpacing = 20 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) }); line.ColumnDefinitions.Add(new ColumnDefinition());
                var caption = Label(name, 12, 0x95a4ad); caption.TextWrapping = TextWrapping.Wrap; line.Children.Add(caption);
                var reading = Label(value, 13, 0xe9eef0); reading.TextWrapping = TextWrapping.Wrap; Grid.SetColumn(reading, 1); line.Children.Add(reading);
                panel.Children.Add(line);
            }
        }
    }
}
