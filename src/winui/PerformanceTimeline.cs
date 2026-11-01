using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly SharedChartTimeline performanceTimeline = new();
    private readonly Button historicalProcessesButton = Button("Procesy v čase…");
    private readonly TextBlock timelineSelection = Label("", 11, 0x95a4ad);
    private HistoricalProcessesWindow? historicalProcessesWindow;

    private FrameworkElement BuildPerformanceTimeline()
    {
        foreach (var plot in new[] { chart, pageFile, coreOverlay, gpuDedicated, gpuShared }) plot.Timeline = performanceTimeline;
        var panel = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 10, 0, 0) };
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timelineSelection.TextWrapping = TextWrapping.Wrap;
        timelineSelection.VerticalAlignment = VerticalAlignment.Center;
        historicalProcessesButton.Height = 29;
        AutomationProperties.SetName(historicalProcessesButton, "Otevřít historické procesy pro vybraný čas nebo úsek");
        AutomationProperties.SetAutomationId(historicalProcessesButton, "historical-processes");
        ToolTipService.SetToolTip(historicalProcessesButton, "Kliknutím na graf připněte čas. Shift+kliknutím vyberte dva konce úseku. Historický přehled neprovádí živé akce.");
        historicalProcessesButton.Click += (_, _) => OpenHistoricalProcesses();
        performanceTimeline.Changed += PerformanceTimelineChanged;
        panel.Children.Add(timelineSelection);
        Grid.SetColumn(historicalProcessesButton, 1); panel.Children.Add(historicalProcessesButton);
        Closed += (_, _) => {
            performanceTimeline.Changed -= PerformanceTimelineChanged;
            historicalProcessesWindow?.Close();
        };
        RefreshTimelineSelection();
        return panel;
    }

    private void PerformanceTimelineChanged(object? sender, ChartTimelineChangedEventArgs args) => RefreshTimelineSelection();

    private void RefreshTimelineSelection()
    {
        var state = performanceTimeline.State;
        historicalProcessesButton.IsEnabled = state.IsPinned && state.CursorTimeNs is not null || performanceTimeline.Interval is not null;
        timelineSelection.Text = performanceTimeline.Interval is { } interval
            ? "Vybraný úsek " + Display.Number(interval.DurationNs / 1_000_000_000d, " s") + " · historický pohled"
            : state.RangeAnchorNs is not null ? "První hranice úseku · Shift+klik nastaví druhou."
            : state.IsPinned ? "Připnutý čas · historický pohled"
            : "Kliknutím připněte čas. Shift+klik vybere úsek.";
    }

    private void OpenHistoricalProcesses()
    {
        long start, end;
        if (performanceTimeline.Interval is { } interval) { start = interval.StartNs; end = interval.EndNs; }
        else if (performanceTimeline.State is { IsPinned: true, CursorTimeNs: { } time }) start = end = time;
        else return;
        var selection = model.SelectProcessHistory(start, end);
        if (historicalProcessesWindow is null) {
            var window = new HistoricalProcessesWindow(selection); historicalProcessesWindow = window;
            window.Closed += (_, _) => { if (ReferenceEquals(historicalProcessesWindow, window)) historicalProcessesWindow = null; };
        } else historicalProcessesWindow.ShowSelection(selection);
        historicalProcessesWindow.Activate();
    }
}
