using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, TextBlock> diskDetailValues = [];
    private Expander? diskDetails;
    private string diskDetailsResource = "";
    private bool diskDetailsExpanded;
    private static readonly string[] DiskSummary = ["Průměrná odezva", "Fronta požadavků", "Čtení IOPS", "Zápis IOPS", "Teplota", "SMART", "Rozhraní", "Kapacita disku"];

    private void RefreshDiskFacts(ResourceRow resource, Action<string, string> fact)
    {
        foreach (string name in DiskSummary)
            fact(name, resource.Stale ? "Zastaralé" : resource.HardwareFacts.TryGetValue(name, out string? value) ? value : "Měření nedostupné");
        if (diskDetails is null) {
            diskDetailValues.Clear();
            var content = new StackPanel { Spacing = 13 };
            foreach (var detail in resource.HardwareFacts.Where(pair => !DiskSummary.Contains(pair.Key))) {
                var field = new StackPanel { Spacing = 3 };
                field.Children.Add(Label(detail.Key, 11, 0x95a4ad));
                var value = Label(detail.Value, 12, 0xe9eef0); value.TextWrapping = TextWrapping.Wrap;
                field.Children.Add(value); diskDetailValues.Add(detail.Key, value); content.Children.Add(field);
            }
            diskDetails = new Expander { Header = Label("Další údaje a SMART", 11, 0xe9eef0), Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, IsExpanded = diskDetailsExpanded };
            diskDetailsResource = resource.Id;
            facts.Children.Add(diskDetails);
        }
        foreach (var detail in diskDetailValues)
            if (resource.HardwareFacts.TryGetValue(detail.Key, out string? value)) detail.Value.Text = resource.Stale ? "Zastaralé" : value;
    }
}
