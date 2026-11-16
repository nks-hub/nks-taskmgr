using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    /// <summary>The production events window over a fixed timeline, and the capture owner without a started ETW session.</summary>
    private static void ProcessEventsWindowAndCapture()
    {
        var now = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Local);
        var timeline = new ProcessTimeline(_ => "tool.exe --flag", () => now);
        var system = TimelineSeen(1, 0, "System", now.AddDays(-1));
        timeline.Observe([system]);
        now = now.AddSeconds(1);
        timeline.Observe([system, TimelineSeen(20, 1, "tool.exe", now)]);
        now = now.AddSeconds(1);
        timeline.Observe([system]);

        var capture = new ProcessCapture(DispatcherQueue.GetForCurrentThread(), timeline, () => (0L, default(BootIdentifier), SampleState.Unavailable));
        var closing = capture.StopForCloseAsync();
        Require(!capture.Active && closing.IsCompletedSuccessfully && closing.GetAwaiter().GetResult() && capture.State == ProcessEventMonitorState.Idle && !capture.NeedsCleanup,
            "A capture that never started waited for cleanup or reported a session.");
        capture.Dispose();

        var window = new ProcessEventsWindow(timeline, null);
        try {
            Require(window.Rows.Count == 2 && window.Rows[0].EventText == Loc.T("Ukončen") && window.Rows[0].CommandText == "tool.exe --flag" && window.Rows[0].ParentText == "System (1)",
                "The events window did not show the timeline with command line and parent.");
            var kinds = EventsWindowElement<ComboBox>(window.Content, "events-kind");
            var search = EventsWindowElement<TextBox>(window.Content, "events-filter");
            kinds.SelectedIndex = 1; window.Refresh(true);
            Require(window.Rows.Count == 1 && window.Rows[0].EventText == Loc.T("Spuštěn"), "The started filter did not show only starts.");
            kinds.SelectedIndex = 3; window.Refresh(true);
            Require(window.Rows.Count == 0, "The repeated filter showed a process that was started once.");
            kinds.SelectedIndex = 0; search.Text = "--flag"; window.Refresh(true);
            Require(window.Rows.Count == 2, "Searching the command line did not find the process.");
            search.Text = "zzz"; window.Refresh(true);
            Require(window.Rows.Count == 0, "Searching for an unknown text kept rows.");
            search.Text = ""; window.Refresh(true);
            Require(EventsWindowElement<TextBlock>(window.Content, "events-status").Text.Contains(Loc.T("Bez práv správce nejsou vidět návratové kódy"), StringComparison.Ordinal) || MainWindow.RunningElevated,
                "The status line did not say plainly what the timeline misses without ETW.");

            var revealed = new List<ProcessKey>();
            window.RevealProcess = key => { revealed.Add(key); return true; };
            Require(window.BuildMenu(window.Rows[0]).Items.Count == 1 && window.BuildMenu(window.Rows[1]).Items.Count == 1, "A process that ended offered to be shown in Procesy.");
            timeline.Observe([system, TimelineSeen(21, 1, "live.exe", now)]);
            var liveRow = ProcessEventRow.Build(timeline.Entries, ProcessEventFilter.Started, "live", new()).Single();
            var liveMenu = window.BuildMenu(liveRow);
            Require(liveMenu.Items.Count == 2 && liveMenu.Items[1] is MenuFlyoutItem { Text: var text } && text == Loc.T("Zobrazit v Procesech"), "A live process did not offer to be shown in Procesy.");
            new Microsoft.UI.Xaml.Automation.Peers.MenuFlyoutItemAutomationPeer((MenuFlyoutItem)liveMenu.Items[1]).Invoke();
            Require(revealed.Count == 1 && revealed[0] == liveRow.Entry.Key, "Show in Procesy did not pass the identity of the live process.");
        } finally { window.Close(); }

        // The historical window lists only the starts and ends between its first and last snapshot.
        long Wall(DateTime value) => new DateTimeOffset(value).ToUnixTimeMilliseconds() * 1_000_000;
        var store = new ProcessHistoryStore();
        var origin = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Local);
        store.Capture(1_000_000_000, Wall(origin.AddMilliseconds(500)), 1, [], null, null);
        store.Capture(2_000_000_000, Wall(origin.AddMilliseconds(1500)), 2, [], null, null);
        var history = new HistoricalProcessesWindow(store.Select(1_000_000_000, 2_000_000_000), "", timeline);
        try {
            var expander = EventsWindowElement<Expander>(history.Content, "historical-lifecycle");
            var lifecycle = EventsWindowElement<ListView>(history.Content, "historical-lifecycle-list");
            Require(expander.Visibility == Visibility.Visible && ReferenceEquals(expander.Content, lifecycle), "The historical window hid the starts and ends of the selected range.");
            string listed = lifecycle.ItemsSource is IEnumerable<string> values ? string.Join(" / ", values) : "";
            Require(lifecycle.ItemsSource is string[] { Length: 1 } items && items[0].Contains("tool.exe", StringComparison.Ordinal) && items[0].Contains(Loc.T("Spuštěn"), StringComparison.Ordinal),
                "The historical window did not list exactly the start inside the selected range: " + listed);
        } finally { history.Close(); }
        App.Trace("Process events window checks passed: rows, filters, search, status line, menu, capture without ETW and starts and ends in a history range");
    }

    private static T EventsWindowElement<T>(DependencyObject root, string automationId) where T : FrameworkElement
    {
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out var item)) {
            if (item is T found && AutomationProperties.GetAutomationId(found) == automationId) return found;
            if (item is Panel panel) foreach (var child in panel.Children) pending.Push(child);
            else if (item is ContentControl { Content: DependencyObject content }) pending.Push(content);
            else if (item is Border { Child: { } borderChild }) pending.Push(borderChild);
        }
        throw new InvalidOperationException("The production events window is missing " + automationId + ".");
    }
}
