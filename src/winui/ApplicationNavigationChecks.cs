using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private async Task CheckApplicationNavigationAsync()
    {
        await NavigationRenderedAsync();
        var originalRows = model.Processes.ToArray();
        var originalState = CaptureProcessNavigation();
        var originalView = view;
        var originalProcesses = processNavigation;
        var originalApplications = applicationNavigation;
        var originalPending = pendingApplicationSelection;
        var restoreInventory = model.Applications.PreserveNavigationInventory();
        var rows = Enumerable.Range(0, 242).Select(index => NavigationProcess((uint)(900000 + index),
            index is 0 or 121 ? 0u : index < 121 ? 900000u : 900121u, "Navigation " + index.ToString("D3")))
            .Concat(Enumerable.Range(0, 28).Select(index => NavigationProcess((uint)(901000 + index), 0, "Navigation Group " + index.ToString("D2")))).ToArray();
        try {
            model.ReplayForBenchmark(rows);
            model.ApplyApplications(new ProcessDetailsSnapshot(ProcessDetailsCommand.Applications, ProcessDetailsState.Ok, 0, 0,
                ProcessArchitecture.X64, false, [], [], null, DateTimeOffset.UtcNow) {
                Applications = new[] { NavigationApplication(rows[0], "Navigation Group Alpha"), NavigationApplication(rows[121], "Navigation Group Beta") }
                    .Concat(rows.Skip(242).Select(row => NavigationApplication(row, row.Name))).ToArray()
            });
            foreach (var mode in new[] { ProcessMode.All, ProcessMode.Active, ProcessMode.Tree }) {
                CancelNavigationRestore(); processNavigation = applicationNavigation = null; pendingApplicationSelection = null;
                string text = mode == ProcessMode.Tree ? "" : "Navigation";
                model.RestoreProcessNavigation(mode, text, 3, true, null, []);
                if (mode == ProcessMode.Tree) model.Toggle(rows[121]);
                processQuery = text; ShowView(DesktopView.Processes); RefreshProcesses();
                await SetNavigationQueryAsync(text);
                SelectNavigationRows(rows[5], rows[8]);
                await ScrollNavigationAsync(model.VisibleProcesses[70]);
                var processes = CaptureProcessNavigation();
                NavigationRequire(processes.VerticalOffset > 0 && processes.Anchor is not null
                    && processes.Anchor != processes.Primary, "Process navigation did not capture a distinct visible scroll anchor.");

                new ButtonAutomationPeer(applicationNav).Invoke();
                await NavigationRenderedAsync();
                NavigationRequire(view == DesktopView.Applications && model.Mode == ProcessMode.Applications
                    && processModeButtons.All(item => item.Button.Visibility == Visibility.Collapsed), "Application navigation did not use its standalone view.");
                applicationChoice.SelectedItem = model.Applications.Visible.Single(group => group.Key == rows[121].Key);
                await SetNavigationQueryAsync("Navigation Group");
                model.Sort(5); model.Toggle(rows[0]); RefreshProcesses();
                SelectNavigationRows(rows[126], rows[129]);
                await ScrollNavigationAsync(model.VisibleProcesses[60]);
                applicationGroups.ScrollIntoView(model.Applications.Visible[12], ScrollIntoViewAlignment.Leading);
                applicationGroups.UpdateLayout(); await NavigationRenderedAsync();
                var applications = CaptureProcessNavigation();
                NavigationRequire(applications.VerticalOffset > 0 && applications.Anchor is not null
                    && applications.Anchor != applications.Primary && applications.Primary != processes.Primary
                    && applications.GroupAnchor is not null && applications.GroupOffset > 0,
                    "Application navigation did not capture its own selection and anchor.");

                new ButtonAutomationPeer(processesNav).Invoke();
                await NavigationRenderedAsync();
                CheckNavigationState(processes, "process return " + mode);
                NavigationRequire(view == DesktopView.Processes
                    && processModeButtons.All(item => item.Button.Visibility == Visibility.Visible), "Process navigation retained application-only controls.");

                new ButtonAutomationPeer(applicationNav).Invoke();
                await NavigationRenderedAsync();
                CheckNavigationState(applications, "application return " + mode);
                new ButtonAutomationPeer(processesNav).Invoke();
                await NavigationRenderedAsync();
                CheckNavigationState(processes, "second process return " + mode);
            }

            new ButtonAutomationPeer(applicationNav).Invoke();
            await NavigationRenderedAsync();
            var replaced = NavigationProcess(rows[5].Key.Pid, rows[5].ParentPid, rows[5].Name, rows[5].Key.Generation + 10000);
            rows[8].SetStatus(true, false);
            model.ReplayForBenchmark(rows.Select(row => ReferenceEquals(row, rows[5]) ? replaced : row));
            new ButtonAutomationPeer(processesNav).Invoke();
            await NavigationRenderedAsync();
            NavigationRequire(SelectedProcess is null && processList.SelectedItems.Count == 0,
                "Navigation restored an ended selection or retargeted a reused PID.");
            App.Trace("Application navigation passed: actual buttons; All/Active/Tree; independent query, sort, collapsed branches, primary/multiselect and visible anchors; reused/ended targets rejected");
        } finally {
            CancelNavigationRestore(); restoreInventory();
            model.ReplayForBenchmark(originalRows);
            model.RestoreProcessNavigation(originalState.Mode, originalState.Query, originalState.SortColumn, originalState.Descending, originalState.Application, originalState.Collapsed);
            processQuery = originalState.Query; search.Text = originalState.Query;
            processNavigation = originalProcesses; applicationNavigation = originalApplications; pendingApplicationSelection = originalPending;
            ShowView(originalView); RefreshProcesses(); RestoreProcessNavigation(originalState);
            await NavigationRenderedAsync();
        }
    }

    private void SelectNavigationRows(ProcessRow primary, ProcessRow secondary)
    {
        SelectProcess(primary, false);
        NavigationRequire(slotsByRow.TryGetValue(secondary, out var slot), "Navigation fixture row is not in the production table.");
        processList.SelectedItems.Add(slot!);
    }

    private async Task SetNavigationQueryAsync(string text)
    {
        if (search.Text != text) {
            var changed = new TaskCompletionSource();
            void TextChanged(object sender, TextChangedEventArgs args) { if (search.Text == text) changed.TrySetResult(); }
            search.TextChanged += TextChanged;
            try { search.Text = text; await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { search.TextChanged -= TextChanged; }
        }
        await NavigationRenderedAsync();
        NavigationRequire(model.ProcessQuery == text && processQuery == text, "Navigation query did not reach the shared model.");
    }

    private async Task ScrollNavigationAsync(ProcessRow row)
    {
        NavigationRequire(slotsByRow.TryGetValue(row, out var slot), "Navigation anchor is not in the production table.");
        processList.ScrollIntoView(slot!, ScrollIntoViewAlignment.Leading);
        processList.UpdateLayout();
        processTable.HorizontalScroll.ChangeView(Math.Min(80, processTable.HorizontalScroll.ScrollableWidth), null, null, true);
        await NavigationRenderedAsync();
    }

    private void CheckNavigationState(ProcessNavigationState expected, string stage)
    {
        var actual = CaptureProcessNavigation();
        App.Trace(FormattableString.Invariant($"application-navigation {stage};mode={actual.Mode};query={actual.Query};sort={actual.SortColumn};descending={actual.Descending};primary={actual.Primary};selected={actual.Selection.Length};anchor={actual.Anchor};top={actual.AnchorTop};offset={actual.VerticalOffset}"));
        NavigationRequire(actual.Mode == expected.Mode && actual.Query == expected.Query && model.ProcessQuery == expected.Query
            && actual.SortColumn == expected.SortColumn && actual.Descending == expected.Descending
            && actual.Collapsed.ToHashSet().SetEquals(expected.Collapsed)
            && actual.Primary == expected.Primary && actual.Selection.ToHashSet().SetEquals(expected.Selection)
            && actual.Application == expected.Application, "Independent navigation state was not restored: " + stage);
        NavigationRequire(actual.Anchor == expected.Anchor && Math.Abs(actual.AnchorTop - expected.AnchorTop) < 1
            && Math.Abs(actual.HorizontalOffset - expected.HorizontalOffset) < 1, "Navigation changed the actual visible anchor: " + stage);
        if (expected.Mode == ProcessMode.Applications) NavigationRequire(actual.GroupAnchor == expected.GroupAnchor
            && Math.Abs(actual.GroupTop - expected.GroupTop) < 1, "Navigation changed the application-group scroll anchor: " + stage);
    }

    private async Task NavigationRenderedAsync()
    {
        var ready = new TaskCompletionSource();
        processList.UpdateLayout();
        var viewers = new[] { Descendant<ScrollViewer>(processList), processTable.HorizontalScroll, Descendant<ScrollViewer>(applicationGroups) }
            .OfType<ScrollViewer>().Distinct().ToArray();
        var changing = new HashSet<ScrollViewer>();
        double[] previous = [];
        int settled = 0;
        void Changing(object? sender, ScrollViewerViewChangingEventArgs args) { if (sender is ScrollViewer scroll) changing.Add(scroll); settled = 0; }
        void Changed(object? sender, ScrollViewerViewChangedEventArgs args) { if (!args.IsIntermediate && sender is ScrollViewer scroll) changing.Remove(scroll); }
        void Frame(object? sender, object args)
        {
            var values = viewers.SelectMany(scroll => new[] { scroll.VerticalOffset, scroll.HorizontalOffset,
                scroll.ViewportHeight, scroll.ExtentHeight, scroll.ViewportWidth, scroll.ExtentWidth }).ToArray();
            bool contentReady = processPage.Visibility != Visibility.Visible || Descendant<ScrollViewer>(processList) is not null;
            bool stable = contentReady && navigationRestoreFrame is null && changing.Count == 0 && values.SequenceEqual(previous);
            settled = stable ? settled + 1 : 0;
            previous = values;
            if (settled >= 2) ready.TrySetResult();
        }
        foreach (var scroll in viewers) { scroll.ViewChanging += Changing; scroll.ViewChanged += Changed; }
        CompositionTarget.Rendering += Frame;
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally {
            foreach (var scroll in viewers) { scroll.ViewChanging -= Changing; scroll.ViewChanged -= Changed; }
            CompositionTarget.Rendering -= Frame;
        }
    }

    private static ProcessApplicationInfo NavigationApplication(ProcessRow row, string title)
        => new((ulong)row.Key.Pid, row.Key, null, title, "", "", SampleState.Unavailable, SampleState.Unavailable);

    private static unsafe ProcessRow NavigationProcess(uint pid, uint parent, string name, ulong generation = 0)
    {
        if (generation == 0) generation = pid;
        var row = new ProcessRow(new(new(51, 52), pid, GenerationKind.CreationTime, generation));
        row.SetCreatedTime(generation);
        fixed (char* text = name) row.Update(new ProcessInfo {
            Pid = pid, ParentPid = parent, Generation = generation, GenerationKind = GenerationKind.CreationTime,
            Name = new String16 { Data = text, Length = (uint)name.Length }, WorkingSetBytes = pid,
            CpuPercent = new Metric { Value = 1, State = SampleState.Valid, Source = MetricSource.NativeProcess },
            ReadIoBytesPerSecond = new Metric { Value = pid, State = SampleState.Valid, Source = MetricSource.NativeProcess }
        });
        return row;
    }

    private static void NavigationRequire(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

internal sealed partial class ApplicationGroupModel
{
    internal Action PreserveNavigationInventory()
    {
        var oldInventory = inventory; var oldSelected = selected; var oldGroups = groups.ToArray(); var oldVisible = Visible.ToArray();
        long oldReceived = receivedAt; bool oldPartial = partial, oldMissing = missingLinks; string oldError = Error;
        return () => {
            Updating = true;
            try {
                inventory = oldInventory; selected = oldSelected; receivedAt = oldReceived; partial = oldPartial; missingLinks = oldMissing; Error = oldError;
                groups.Clear(); foreach (var pair in oldGroups) groups.Add(pair.Key, pair.Value);
                ApplyVisible(oldVisible);
            } finally { Updating = false; }
        };
    }
}
