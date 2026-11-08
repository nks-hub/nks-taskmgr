using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly StackPanel diskTransferPanel = new() { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ChartView diskTransfers = new() { Height = 150 };
    private readonly ToggleButton diskTransferScale = Choice(Loc.T("Auto přenos"));
    private readonly Dictionary<string, (bool Automatic, double Maximum)> diskTransferScales = [];
    private string? diskTransferResource;
    private double diskTransferMaximum = 1_048_576;
    private readonly Dictionary<string, TextBlock> diskDetailValues = [];
    private Expander? diskDetails;
    private string diskDetailsResource = "";
    private bool diskDetailsExpanded;
    private static readonly string[] DiskSummary = ["Průměrná odezva", "Fronta požadavků", "Čtení IOPS", "Zápis IOPS", "Teplota", "SMART", "Rozhraní", "Kapacita disku"];

    private FrameworkElement BuildDiskTransfers()
    {
        var heading = new Grid { ColumnSpacing = 10 };
        heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        CompactPerformanceChoice(diskTransferScale); diskTransferScale.Height = 23; diskTransferScale.FontSize = 11;
        diskTransferScale.IsChecked = true;
        ToolTipService.SetToolTip(diskTransferScale, Loc.T("Automatické měřítko podle čtení i zápisu. Vypnutí podrží aktuální rozsah osy."));
        diskTransferScale.Click += (_, _) => {
            if (selectedResource is { Kind: NksTaskMgr.Native.ResourceKind.Disk } resource)
                diskTransferScales[resource.Id] = (diskTransferScale.IsChecked == true, diskTransferMaximum);
            RefreshPerformance();
        };
        Grid.SetColumn(diskTransferScale, 1); heading.Children.Add(diskTransferScale); diskTransferPanel.Children.Add(heading);
        diskTransfers.Timeline = performanceTimeline; diskTransferPanel.Children.Add(diskTransfers);
        return diskTransferPanel;
    }

    private void RefreshDiskTransfers(ResourceRow resource, long now)
    {
        if (diskTransferResource != resource.Id) {
            diskTransferResource = resource.Id;
            var saved = diskTransferScales.TryGetValue(resource.Id, out var value) ? value : (true, 1_048_576d);
            diskTransferScale.IsChecked = saved.Item1; diskTransferMaximum = saved.Item2;
        }
        if (diskTransferScale.IsChecked == true) {
            long start = now - rangeSeconds * 1_000_000_000L;
            double observed = Math.Max(resource.DiskReadHistory.Maximum(start, 1_048_576), resource.DiskWriteHistory.Maximum(start, 1_048_576));
            diskTransferMaximum = ChartView.ScaleMaximum(observed / 1_048_576, double.MaxValue) * 1_048_576;
        }
        diskTransfers.Configure(Loc.T("Čtení"), ChartUnit.BytesPerSecond, secondName: Loc.T("Zápis"));
        diskTransfers.Set(resource.DiskReadHistory, resource.DiskWriteHistory, now, rangeSeconds, diskTransferMaximum, 0xdfaf76, area: false);
    }

    private Grid diskDetailGrid = new();
    private readonly List<FrameworkElement> diskDetailCells = [];

    /// <summary>Lays the expanded disk details out in the same columns as the facts above them.</summary>
    private void ArrangeDiskDetails()
    {
        int columns = Math.Max(1, performanceFactColumns);
        if (diskDetailGrid.ColumnDefinitions.Count != columns) {
            diskDetailGrid.ColumnDefinitions.Clear();
            for (int i = 0; i < columns; ++i) diskDetailGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        int rows = (diskDetailCells.Count + columns - 1) / columns;
        while (diskDetailGrid.RowDefinitions.Count < rows) diskDetailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (diskDetailGrid.RowDefinitions.Count > rows) diskDetailGrid.RowDefinitions.RemoveAt(diskDetailGrid.RowDefinitions.Count - 1);
        for (int i = 0; i < diskDetailCells.Count; ++i) { Grid.SetColumn(diskDetailCells[i], i % columns); Grid.SetRow(diskDetailCells[i], i / columns); }
    }

    private void RefreshDiskFacts(ResourceRow resource, Action<string, string> fact)
    {
        foreach (string name in DiskSummary)
            fact(Loc.T(name), resource.Stale ? Loc.T("Zastaralé") : resource.HardwareFacts.TryGetValue(name, out string? value) ? Loc.Native(value) : Loc.T("Měření nedostupné"));
        if (diskDetails is null) {
            diskDetailValues.Clear();
            diskDetailCells.Clear(); diskDetailGrid = new Grid { ColumnSpacing = 16, RowSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var detail in resource.HardwareFacts.Where(pair => !DiskSummary.Contains(pair.Key))) {
                var field = new StackPanel { Spacing = 1 };
                var caption = Label(Loc.T(detail.Key), 10, 0x95a4ad); caption.TextWrapping = TextWrapping.Wrap; field.Children.Add(caption);
                var value = Label(Loc.T(detail.Value), 13, 0xe9eef0); value.TextWrapping = TextWrapping.Wrap;
                field.Children.Add(value); diskDetailValues.Add(detail.Key, value); diskDetailGrid.Children.Add(field); diskDetailCells.Add(field);
            }
            diskDetails = new Expander { Header = Label(Loc.T("Další údaje a SMART"), 11, 0xe9eef0), Content = diskDetailGrid,
                Padding = new Thickness(0, 8, 0, 4), BorderThickness = new Thickness(0), Background = flatPerformanceBackground,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = diskDetailsExpanded };
            ArrangeDiskDetails();
            diskDetailsResource = resource.Id;
            facts.Children.Add(diskDetails);
        }
        foreach (var detail in diskDetailValues)
            if (resource.HardwareFacts.TryGetValue(detail.Key, out string? value)) detail.Value.Text = resource.Stale ? Loc.T("Zastaralé") : Loc.Native(value);
    }
}
