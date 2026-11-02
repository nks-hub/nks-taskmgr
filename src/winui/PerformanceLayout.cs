using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Grid performanceFacts = new() { ColumnSpacing = 20, RowSpacing = 12 };
    private readonly List<FrameworkElement> performanceFactCells = [];
    private readonly Dictionary<string, bool> performanceScales = [];
    private string? performanceScaleResource;
    private double performanceDetailWidth, performancePlotHeight = 250;
    private int performanceFactColumns;
    private int compactConsumerCount;
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush flatPerformanceBackground = Display.Brush(0x171d21);

    private void BuildPerformance()
    {
        performancePage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(262) });
        performancePage.ColumnDefinitions.Add(new ColumnDefinition()); performancePage.ColumnSpacing = 22;
        var inventory = new Grid { Padding = new Thickness(0, 0, 10, 0), BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(0, 0, 1, 0) };
        inventory.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        inventory.RowDefinitions.Add(new RowDefinition());
        var inventoryHeader = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        var inventoryLabel = Label("Prostředky", 11, 0x95a4ad); inventoryLabel.VerticalAlignment = VerticalAlignment.Center;
        resourceCount.FontSize = 11; resourceCount.HorizontalAlignment = HorizontalAlignment.Right; resourceCount.VerticalAlignment = VerticalAlignment.Center;
        inventoryHeader.Children.Add(inventoryLabel); inventoryHeader.Children.Add(resourceCount); inventory.Children.Add(inventoryHeader);
        ConfigureNetworkFilters(inventoryHeader);
        Grid.SetRow(resourceList, 1); inventory.Children.Add(resourceList); performancePage.Children.Add(inventory);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = performanceBody };
        performanceBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        performanceBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        performanceBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        performanceBody.RowSpacing = 18;
        Grid.SetColumn(scroll, 1); performancePage.Children.Add(scroll);
        var content = new StackPanel();
        var resourceHeading = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 2) };
        resourceHeading.ColumnDefinitions.Add(new ColumnDefinition()); resourceHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headingText = new StackPanel { Spacing = 3 };
        resourceTitle.FontSize = 22; resourceTitle.CharacterSpacing = -15; resourceTitle.TextTrimming = TextTrimming.CharacterEllipsis; headingText.Children.Add(resourceTitle);
        headingText.Children.Add(resourceDescription); resourceDescription.FontSize = 11; resourceDescription.TextTrimming = TextTrimming.CharacterEllipsis;
        resourceHeading.Children.Add(headingText);
        scaleChoice.IsChecked = false; scaleChoice.Height = 25;
        scaleChoice.Click += (_, _) => SetPerformanceAutomaticScale(scaleChoice.IsChecked == true);
        ToolTipService.SetToolTip(scaleChoice, "Automaticky podle uložených vzorků. Vypnuto: 0–100 %, kapacita paměti nebo rychlost připojení.");
        chartControls.Children.Add(scaleChoice);
        foreach (int seconds in new[] { 60, 600 }) {
            var button = Choice(seconds == 60 ? "60 s" : "10 min"); button.Height = 25; rangeButtons.Add(button);
            button.Click += (_, _) => { rangeSeconds = seconds; RefreshPerformance(); }; chartControls.Children.Add(button);
        }
        var reading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 190 };
        resourceValue.CharacterSpacing = -15; reading.Children.Add(resourceValue);
        resourceUnit.FontSize = 11; resourceUnit.VerticalAlignment = VerticalAlignment.Bottom; resourceUnit.Margin = new Thickness(0, 0, 0, 3); reading.Children.Add(resourceUnit);
        Grid.SetColumn(reading, 1); resourceHeading.Children.Add(reading); content.Children.Add(resourceHeading);
        foreach (var mode in new[] { "Celkem", "Logické", "Překrytí" }) {
            int index = cpuModes.Children.Count; var button = Choice(mode); button.Height = 25; cpuButtons.Add(button);
            button.Click += (_, _) => { cpuMode = index; RefreshPerformance(); }; cpuModes.Children.Add(button);
        }
        chartControls.Spacing = 4; plotToolbar.Margin = new Thickness(0, 9, 0, 9); plotToolbar.RowSpacing = 5;
        plotToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); plotToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        plotToolbar.ColumnDefinitions.Add(new ColumnDefinition()); plotToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        plotToolbar.Children.Add(cpuModes); Grid.SetColumn(chartControls, 1); plotToolbar.Children.Add(chartControls);
        content.Children.Add(plotToolbar); content.Children.Add(chart); content.Children.Add(processors); content.Children.Add(BuildGpu()); content.Children.Add(BuildDiskTransfers());
        processors.Visibility = gpuPanel.Visibility = diskTransferPanel.Visibility = Visibility.Collapsed;
        pageFile.Visibility = pageFileTitle.Visibility = Visibility.Collapsed;
        pageFileTitle.Margin = new Thickness(0, 12, 0, 5);
        content.Children.Add(pageFileTitle); content.Children.Add(pageFile);
        retained.FontSize = 11; retained.Margin = new Thickness(0, 5, 0, 0); content.Children.Add(retained);
        content.Children.Add(BuildPerformanceTimeline());
        chartGrid.Children.Add(content);
        performanceBody.Children.Add(chartGrid);
        facts.Padding = new Thickness(0); facts.Spacing = 12; Grid.SetRow(facts, 1); performanceBody.Children.Add(facts);
        consumers.Spacing = 8; Grid.SetRow(consumers, 2); performanceBody.Children.Add(consumers);
        AutomationProperties.SetName(inventory, "Připojené a zobrazené prostředky s historií využití");
        page.Children.Add(performancePage);
    }

    private void ResizePerformanceLayout(double width)
    {
        double inventoryWidth = width <= 780 ? 210 : width <= 1050 ? 234 : 262;
        performancePage.ColumnDefinitions[0].Width = new GridLength(inventoryWidth);
        performancePage.ColumnSpacing = width <= 780 ? 12 : 22;
        performanceDetailWidth = Math.Max(1, width - root.ColumnDefinitions[0].Width.Value
            - page.Margin.Left - page.Margin.Right - inventoryWidth - performancePage.ColumnSpacing);
        bool stackedToolbar = performanceDetailWidth < 620;
        Grid.SetRow(chartControls, stackedToolbar ? 1 : 0); Grid.SetColumn(chartControls, stackedToolbar ? 0 : 1);
        Grid.SetColumnSpan(chartControls, stackedToolbar ? 2 : 1);
        chartControls.HorizontalAlignment = stackedToolbar ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        performancePlotHeight = Math.Clamp(root.ActualHeight * .31, 190, 290);
        bool disk = selectedResource?.Kind == ResourceKind.Disk;
        chart.Height = disk ? Math.Clamp(performancePlotHeight * .66, 140, 190) : performancePlotHeight;
        coreOverlay.Height = performancePlotHeight;
        diskTransfers.Height = Math.Clamp(performancePlotHeight * .59, 130, 170);
        pageFile.Height = Math.Clamp(performancePlotHeight * .38, 90, 110);
        ArrangePerformanceFacts();
    }

    private void ArrangePerformanceFacts()
    {
        int columns = Math.Clamp((int)(performanceDetailWidth / 165), 1, 4);
        if (performanceFactColumns != columns) {
            performanceFactColumns = columns; performanceFacts.ColumnDefinitions.Clear();
            for (int i = 0; i < columns; ++i) performanceFacts.ColumnDefinitions.Add(new ColumnDefinition());
        }
        int rows = (performanceFactCells.Count + columns - 1) / columns;
        while (performanceFacts.RowDefinitions.Count < rows) performanceFacts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (performanceFacts.RowDefinitions.Count > rows) performanceFacts.RowDefinitions.RemoveAt(performanceFacts.RowDefinitions.Count - 1);
        for (int i = 0; i < performanceFactCells.Count; ++i) {
            Grid.SetColumn(performanceFactCells[i], i % columns); Grid.SetRow(performanceFactCells[i], i / columns);
        }
    }

    private void SelectPerformanceScale(ResourceRow resource)
    {
        if (performanceScaleResource == resource.Id) return;
        performanceScaleResource = resource.Id;
        scaleChoice.IsChecked = performanceScales.TryGetValue(resource.Id, out bool automatic) ? automatic : resource.Kind == ResourceKind.Network;
    }

    private void SetPerformanceAutomaticScale(bool automatic)
    {
        scaleChoice.IsChecked = automatic;
        if (selectedResource is { } resource) performanceScales[resource.Id] = automatic;
        RefreshPerformance();
    }

    private void CompactPerformanceConsumers()
    {
        if (compactConsumerCount == consumerItems.Count) return;
        compactConsumerCount = consumerItems.Count;
        consumerHeading.Visibility = Visibility.Collapsed; consumerCaption.FontSize = 11;
        consumerCards.ColumnSpacing = 10;
        foreach (var item in consumerItems) {
            item.Button.Height = 44; item.Button.Padding = new Thickness(6, 4, 6, 4);
            item.Button.BorderThickness = new Thickness(0); item.Button.CornerRadius = new CornerRadius(0);
            item.Button.Background = flatPerformanceBackground;
            item.Title.TextTrimming = item.Pid.TextTrimming = TextTrimming.CharacterEllipsis;
        }
    }
}
