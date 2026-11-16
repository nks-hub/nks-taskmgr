using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly SharedChartTimeline performanceTimeline = new();
    private readonly Button historicalProcessesButton = Button(Loc.T("Procesy v čase…"));
    private readonly TextBlock timelineSelection = Label("", 11, 0x95a4ad);
    private readonly Grid performanceTimelinePanel = new() { ColumnSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
    private HistoricalProcessesWindow? historicalProcessesWindow;

    private FrameworkElement BuildPerformanceTimeline()
    {
        foreach (var plot in new[] { chart, pageFile, coreOverlay, gpuDedicated, gpuShared }) plot.Timeline = performanceTimeline;
        performanceTimeline.RangeDragged += (_, _) => OpenHistoricalProcesses();
        var panel = performanceTimelinePanel;
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timelineSelection.TextWrapping = TextWrapping.Wrap;
        timelineSelection.VerticalAlignment = VerticalAlignment.Center;
        historicalProcessesButton.Height = 25; historicalProcessesButton.Padding = new Thickness(8, 0, 8, 0);
        historicalProcessesButton.BorderThickness = new Thickness(0); historicalProcessesButton.CornerRadius = new CornerRadius(3);
        AutomationProperties.SetName(historicalProcessesButton, Loc.T("Otevřít historické procesy pro vybraný čas nebo úsek"));
        AutomationProperties.SetAutomationId(historicalProcessesButton, "historical-processes");
        ToolTipService.SetToolTip(historicalProcessesButton, Loc.T("Kliknutím do grafu připnete čas. Tažením nebo Shift+kliknutím vyberete úsek. Historický přehled je jen ke čtení."));
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
            ? Loc.F("Vybraný úsek {0} · historický pohled", Display.Number(interval.DurationNs / 1_000_000_000d, " s"))
            : state.RangeAnchorNs is not null ? Loc.T("První hranice úseku · Shift+klik nastaví druhou.")
            : state.IsPinned ? Loc.T("Připnutý čas · historický pohled") : "";
        // The row appears only once a time is pinned; how to pin is in the range buttons' tooltip.
        var visibility = timelineSelection.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (performanceTimelinePanel.Visibility != visibility) performanceTimelinePanel.Visibility = visibility;
    }

    /// <summary>Whole-system CPU and RAM over the range, from the same resource history the charts draw.</summary>
    private string SystemSummary(long start, long end)
    {
        var parts = new List<string>(2);
        foreach (var row in model.Resources) {
            if (row.Kind is not (ResourceKind.Cpu or ResourceKind.Memory) || row.History.Summary(start, end) is not { } value) continue;
            bool memory = row.Kind == ResourceKind.Memory; double scale = memory ? 1073741824 : 1;
            parts.Add((memory ? "RAM " : "CPU ") + Display.Number(value.Average / scale, "", 1) + " / " + Display.Number(value.Peak / scale, memory ? " GiB" : " %", 1));
        }
        return parts.Count == 0 ? "" : Loc.F("Systém Ø / max: {0}", string.Join("  ·  ", parts));
    }

    private void OpenHistoricalProcesses()
    {
        long start, end;
        if (performanceTimeline.Interval is { } interval) { start = interval.StartNs; end = interval.EndNs; }
        else if (performanceTimeline.State is { IsPinned: true, CursorTimeNs: { } time }) start = end = time;
        else return;
        var selection = model.SelectProcessHistory(start, end);
        string system = start == end ? "" : SystemSummary(start, end);
        if (historicalProcessesWindow is null) {
            var window = new HistoricalProcessesWindow(selection, system, processTimeline); historicalProcessesWindow = window;
            window.Closed += (_, _) => { if (ReferenceEquals(historicalProcessesWindow, window)) historicalProcessesWindow = null; };
        } else historicalProcessesWindow.ShowSelection(selection, system);
        historicalProcessesWindow.Activate();
    }
}
