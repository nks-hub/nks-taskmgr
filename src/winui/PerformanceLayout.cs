using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private const double PerformanceScrollGutter = 20;
    private readonly ScrollViewer performanceDetailScroll = new() {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Thickness(0, 0, PerformanceScrollGutter, 0)
    };
    private readonly Grid performanceFacts = new() { ColumnSpacing = 16, RowSpacing = 8 };
    private readonly Grid performanceHeading = new() { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 2) };
    private readonly StackPanel performanceReading = new() { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 190 };
    private readonly List<FrameworkElement> performanceFactCells = [];
    private readonly Dictionary<string, bool> performanceScales = [];
    private string? performanceScaleResource;
    private double performanceDetailWidth, performancePlotHeight = 250;
    private int performanceFactColumns;
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush flatPerformanceBackground = Display.Brush(0x171d21);

    private void BuildPerformance()
    {
        performancePage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(262) });
        performancePage.ColumnDefinitions.Add(new ColumnDefinition()); performancePage.ColumnSpacing = 18;
        var inventory = new Grid { Padding = new Thickness(0, 0, 10, 0), BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(0, 0, 1, 0) };
        inventory.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        inventory.RowDefinitions.Add(new RowDefinition());
        var inventoryHeader = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        var inventoryLabel = Label(Loc.T("Prostředky"), 11, 0x95a4ad); inventoryLabel.VerticalAlignment = VerticalAlignment.Center;
        resourceCount.FontSize = 11; resourceCount.HorizontalAlignment = HorizontalAlignment.Right; resourceCount.VerticalAlignment = VerticalAlignment.Center;
        inventoryHeader.Children.Add(inventoryLabel); inventoryHeader.Children.Add(resourceCount); inventory.Children.Add(inventoryHeader);
        ConfigureNetworkFilters(inventoryHeader);
        Grid.SetRow(resourceList, 1); inventory.Children.Add(resourceList); performancePage.Children.Add(inventory);
        performanceDetailScroll.Content = performanceBody;
        AutomationProperties.SetAutomationId(performanceDetailScroll, "performance-detail-scroll");
        AutomationProperties.SetAutomationId(performanceBody, "performance-detail-content");
        AutomationProperties.SetAutomationId(performanceFacts, "performance-facts");
        performanceBody.SizeChanged += (_, args) => {
            if (Math.Abs(args.NewSize.Width - args.PreviousSize.Width) < .1) return;
            ResizePerformanceDetail();
            if (performance) RefreshPerformance();
        };
        performanceBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        performanceBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        performanceBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        performanceBody.RowSpacing = 12;
        Grid.SetColumn(performanceDetailScroll, 1); performancePage.Children.Add(performanceDetailScroll);
        var content = new StackPanel();
        performanceHeading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); performanceHeading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        performanceHeading.ColumnDefinitions.Add(new ColumnDefinition()); performanceHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        performanceHeading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var headingText = performanceHeadingText;
        resourceTitle.FontSize = 20; resourceTitle.CharacterSpacing = -15; resourceTitle.TextTrimming = TextTrimming.CharacterEllipsis; headingText.Children.Add(resourceTitle);
        headingText.Children.Add(resourceDescription); resourceDescription.FontSize = 11; resourceDescription.TextTrimming = TextTrimming.CharacterEllipsis;
        performanceHeading.Children.Add(headingText);
        scaleChoice.IsChecked = false; CompactPerformanceChoice(scaleChoice);
        scaleChoice.Click += (_, _) => SetPerformanceAutomaticScale(scaleChoice.IsChecked == true);
        ToolTipService.SetToolTip(scaleChoice, Loc.T("Automaticky podle uložených vzorků. Vypnuto: 0–100 %, kapacita paměti nebo rychlost připojení."));
        chartControls.Children.Add(scaleChoice);
        foreach (int seconds in new[] { 60, 600 }) {
            var button = Choice(seconds == 60 ? "60 s" : "10 min"); CompactPerformanceChoice(button); rangeButtons.Add(button);
            button.Click += (_, _) => { rangeSeconds = seconds; RefreshPerformance(); }; chartControls.Children.Add(button);
        }
        resourceEyebrow.FontSize = 10; resourceEyebrow.VerticalAlignment = VerticalAlignment.Center;
        performanceReading.Children.Add(resourceEyebrow);
        resourceValue.FontSize = 18; resourceValue.CharacterSpacing = -15; performanceReading.Children.Add(resourceValue);
        resourceUnit.FontSize = 11; resourceUnit.VerticalAlignment = VerticalAlignment.Bottom; resourceUnit.Margin = new Thickness(0, 0, 0, 2); performanceReading.Children.Add(resourceUnit);
        Grid.SetColumn(performanceReading, 1); performanceHeading.Children.Add(performanceReading); content.Children.Add(performanceHeading);
        foreach (var mode in new[] { Loc.T("Celkem"), Loc.T("Logické"), Loc.T("Překrytí") }) {
            int index = cpuModes.Children.Count; var button = Choice(mode); CompactPerformanceChoice(button); cpuButtons.Add(button);
            button.Click += (_, _) => { cpuMode = index; RefreshPerformance(); }; cpuModes.Children.Add(button);
        }
        chartControls.Spacing = 4; plotToolbar.Margin = new Thickness(0, 7, 0, 8); plotToolbar.RowSpacing = 5;
        plotToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); plotToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        plotToolbar.ColumnDefinitions.Add(new ColumnDefinition()); plotToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        plotToolbar.Children.Add(cpuModes); Grid.SetColumn(chartControls, 1); plotToolbar.Children.Add(chartControls);
        content.Children.Add(plotToolbar); content.Children.Add(chart); content.Children.Add(processors); content.Children.Add(BuildGpu()); content.Children.Add(BuildDiskTransfers());
        processors.Visibility = gpuPanel.Visibility = diskTransferPanel.Visibility = Visibility.Collapsed;
        pageFile.Visibility = pageFileTitle.Visibility = Visibility.Collapsed;
        pageFileTitle.Margin = new Thickness(0, 12, 0, 5);
        content.Children.Add(pageFileTitle); content.Children.Add(pageFile);
        content.Children.Add(BuildPerformanceTimeline());
        chartGrid.Children.Add(content);
        performanceBody.Children.Add(chartGrid);
        facts.Padding = new Thickness(0); facts.Spacing = 8; Grid.SetRow(facts, 1); performanceBody.Children.Add(facts);
        consumers.Spacing = 5; Grid.SetRow(consumers, 2); performanceBody.Children.Add(consumers);
        AutomationProperties.SetName(inventory, Loc.T("Připojené a zobrazené prostředky s historií využití"));
        page.Children.Add(performancePage);
    }

    private readonly StackPanel performanceHeadingText = new() { Spacing = 3 };

    /// <summary>Puts the range and scale buttons beside the title, except for CPU whose view switch needs the toolbar row.</summary>
    private void PlaceChartControls(bool toolbar)
    {
        Panel target = toolbar ? plotToolbar : performanceHeading;
        if (ReferenceEquals(chartControls.Parent, target)) return;
        (chartControls.Parent as Panel)?.Children.Remove(chartControls);
        target.Children.Add(chartControls);
        plotToolbar.Visibility = toolbar ? Visibility.Visible : Visibility.Collapsed;
        ResizePerformanceDetail();
    }

    private void ResizePerformanceLayout(double width)
    {
        double inventoryWidth = width <= 780 ? 210 : width <= 1050 ? 234 : 262;
        performancePage.ColumnDefinitions[0].Width = new GridLength(inventoryWidth);
        performancePage.ColumnSpacing = width <= 780 ? 12 : 18;
        ResizePerformanceDetail();
    }

    private static void CompactPerformanceChoice(Microsoft.UI.Xaml.Controls.Primitives.ToggleButton button)
    {
        button.Height = 25; button.Padding = new Thickness(9, 0, 9, 0);
        button.BorderThickness = new Thickness(0); button.CornerRadius = new CornerRadius(3);
    }

    private void ResizePerformanceDetail()
    {
        if (performanceBody.ActualWidth <= 0) return;
        performanceDetailWidth = performanceBody.ActualWidth;
        bool stackedHeading = performanceDetailWidth < 420;
        Grid.SetRow(performanceReading, stackedHeading ? 1 : 0); Grid.SetColumn(performanceReading, stackedHeading ? 0 : 1);
        Grid.SetColumnSpan(performanceReading, stackedHeading ? 2 : 1);
        performanceReading.HorizontalAlignment = stackedHeading ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        performanceHeading.RowSpacing = stackedHeading ? 3 : 0;
        bool inHeading = ReferenceEquals(chartControls.Parent, performanceHeading);
        bool stackedToolbar = inHeading ? stackedHeading : performanceDetailWidth < 560;
        Grid.SetRowSpan(performanceHeadingText, inHeading && !stackedHeading ? 2 : 1);
        Grid.SetRow(chartControls, inHeading ? stackedHeading ? 2 : 1 : stackedToolbar ? 1 : 0); Grid.SetColumn(chartControls, stackedToolbar ? 0 : 1);
        Grid.SetColumnSpan(chartControls, stackedToolbar ? 2 : 1);
        chartControls.HorizontalAlignment = stackedToolbar ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        chartControls.Margin = inHeading ? new Thickness(0, 4, 0, 6) : new Thickness(0);
        chartControls.Orientation = cpuModes.Orientation = performanceDetailWidth < 240 ? Orientation.Vertical : Orientation.Horizontal;
        Grid.SetRow(historicalProcessesButton, stackedHeading ? 1 : 0); Grid.SetColumn(historicalProcessesButton, stackedHeading ? 0 : 1);
        Grid.SetColumnSpan(historicalProcessesButton, stackedHeading ? 2 : 1);
        historicalProcessesButton.HorizontalAlignment = stackedHeading ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        performanceTimelinePanel.RowSpacing = stackedHeading ? 4 : 0;
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

}
