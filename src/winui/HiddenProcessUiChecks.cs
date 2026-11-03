using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private async Task CheckHiddenProcessUiAsync()
    {
        NavigationRequire(processBranchCheckRunning, "Hidden process UI checks require the suspended process timer.");
        await NavigationRenderedAsync();
        var originalState = CaptureProcessNavigation();
        var originalView = view;
        var originalProcesses = processNavigation;
        var originalApplications = applicationNavigation;
        var originalPending = pendingApplicationSelection;
        var originalRestore = pendingNavigationRestore;
        var originalValueMode = model.TableValueMode;
        var originalResource = resourceList.SelectedItem;
        bool originalHeld = model.Held;
        try {
            ShowView(DesktopView.Processes);
            await SetNavigationQueryAsync("");
            model.RestoreProcessNavigation(ProcessMode.Tree, "", 3, true, null, []);
            model.SetTableValueMode(ProcessTableValueMode.ProcessWithDescendants);
            RefreshProcesses();
            await NavigationRenderedAsync();
            var branch = model.VisibleProcesses.FirstOrDefault(row => row.HasChildren && model.IsCurrent(row) && HasNavigationIdentity(row))
                ?? throw new InvalidOperationException("Hidden navigation has no verified live branch.");
            model.Toggle(branch); RefreshProcesses();
            var rows = model.VisibleProcesses.Where(row => model.IsCurrent(row) && HasNavigationIdentity(row)).ToArray();
            NavigationRequire(rows.Length > 8, "Hidden navigation has too few current rows for selection and scrolling.");
            SelectNavigationRows(rows[2], rows[5]);
            await ScrollNavigationAsync(rows[rows.Length / 2], 0);
            var expected = CaptureProcessNavigation();
            var scroll = Descendant<ScrollViewer>(processList);
            NavigationRequire(expected.Primary == rows[2].Key && expected.Selection.Length == 2
                && expected.Collapsed.Contains(branch.Key) && expected.Anchor is not null
                && (scroll is not { ScrollableHeight: > 0 } || expected.VerticalOffset > 0),
                "Hidden navigation did not establish an actual tree selection, collapse and visible anchor.");

            new ButtonAutomationPeer(performanceNav).Invoke();
            await NavigationRenderedAsync();
            new ButtonAutomationPeer(hardwareNav).Invoke();
            await NavigationRenderedAsync();
            NavigationRequire(navigationRestoreFrame is null && pendingNavigationRestore is not null,
                "A hidden tree retained its rendering callback or lost its pending navigation.");
            new ButtonAutomationPeer(processesNav).Invoke();
            await NavigationRenderedAsync();
            CheckNavigationState(expected, "hidden process tree through performance and hardware");
            NavigationRequire(model.TableValueMode == ProcessTableValueMode.ProcessWithDescendants && branch.Collapsed,
                "A hidden tree return lost its branch-value preference or collapsed branch.");
            WinUiChecks.HiddenProcessBoundRows(processTable, processList);

            resourceList.SelectedItem = model.Resources.First(row => row.Kind == ResourceKind.Cpu);
            ShowView(DesktopView.Performance);
            await NavigationRenderedAsync();
            var card = consumerItems.FirstOrDefault(item => item.Current(model) is not null)
                ?? throw new InvalidOperationException("Performance has no current process consumer to navigate to.");
            var target = card.Current(model) ?? throw new InvalidOperationException("The process consumer lost its current identity.");
            new ButtonAutomationPeer(card.Button).Invoke();
            await NavigationRenderedAsync();
            NavigationRequire(view == DesktopView.Processes && model.Mode == ProcessMode.All
                && SelectedProcess is { } selected && ReferenceEquals(selected, target) && selected.Key == target.Key && model.IsCurrent(target),
                "A performance card did not select its exact existing process after the hidden return.");
            WinUiChecks.HiddenProcessBoundRows(processTable, processList);

            await CheckHiddenProcessBindingsAsync();
            App.Trace("Hidden process UI passed: actual tree/performance/hardware return, full-key selection, collapse, branch mode, sort/anchor and existing consumer; isolated Apply/visibility setter refreshed seven bound cells without rebuilding slots");
        } finally {
            CancelNavigationRestore();
            model.SetTableValueMode(originalValueMode);
            model.RestoreProcessNavigation(originalState.Mode, originalState.Query, originalState.SortColumn,
                originalState.Descending, originalState.Application, originalState.Collapsed);
            processQuery = originalState.Query; SetNavigationSearchText(originalState.Query);
            processNavigation = originalProcesses; applicationNavigation = originalApplications; pendingApplicationSelection = originalPending;
            resourceList.SelectedItem = originalResource;
            ShowView(originalView); RefreshProcesses();
            RestoreProcessNavigation(originalRestore ?? originalState);
            await NavigationRenderedAsync();
            model.SetHeld(originalHeld);
        }
    }

    private async Task CheckHiddenProcessBindingsAsync()
    {
        using var source = new MonitorModel();
        ApplyHiddenProcessUiFrame(source, 0);
        source.Sort(1);
        var detach = PreserveProcessView(source);
        try {
            source.VisibleProcesses.RefreshView();
            var target = source.VisibleProcesses[0];
            SelectProcess(target);
            await NavigationRenderedAsync();
            WinUiChecks.HiddenProcessBoundRows(processTable, processList);
            var slots = processSlots.ToArray();
            var rows = source.VisibleProcesses.ToArray();
            var before = WinUiChecks.HiddenProcessCellTexts(processList, slots[0]);
            source.SetProcessViewVisible(false);
            ApplyHiddenProcessUiFrame(source, 1);
            await NavigationRenderedAsync();
            NavigationRequire(target.Cpu.Value == 35 && target.WorkingBytes == 64UL * 1048576
                && rows.SequenceEqual(source.VisibleProcesses, ReferenceEqualityComparer.Instance)
                && slots.SequenceEqual(processSlots, ReferenceEqualityComparer.Instance)
                && before.SequenceEqual(WinUiChecks.HiddenProcessCellTexts(processList, slots[0])),
                "An isolated hidden frame changed bound cells/slots or failed to update raw metrics.");

            // The fixture owns its model; production ShowView is covered by the live roundtrip above.
            source.SetProcessViewVisible(true);
            await NavigationRenderedAsync();
            NavigationRequire(slots.SequenceEqual(processSlots, ReferenceEqualityComparer.Instance)
                && !before.SequenceEqual(WinUiChecks.HiddenProcessCellTexts(processList, slots[0])),
                "Showing unchanged row order rebuilt slots or left their old bound values.");
            WinUiChecks.HiddenProcessBoundRows(processTable, processList);
        } finally {
            detach();
            model.VisibleProcesses.RefreshView();
        }
    }

    private static unsafe void ApplyHiddenProcessUiFrame(MonitorModel source, int revision)
    {
        string name = revision == 0 ? "Hidden UI before" : "Hidden UI after";
        fixed (char* text = name) {
            var rows = new ProcessInfo[3];
            for (int i = 0; i < rows.Length; ++i) {
                var value = WinUiChecks.Process((uint)(910000 + i), (ulong)(1000 + i), 5 + revision * 30 + i);
                value.Name = new String16 { Data = text, Length = (uint)name.Length };
                value.WorkingSetBytes = (ulong)(16 + revision * 48 + i) * 1048576;
                value.PrivateBytes = (ulong)(8 + revision * 24 + i) * 1048576;
                value.ReadIoBytesPerSecond = new Metric { Value = (revision + 1) * 1048576, State = SampleState.Valid };
                value.WriteIoBytesPerSecond = new Metric { Value = (revision + 2) * 1048576, State = SampleState.Valid };
                rows[i] = value;
            }
            WinUiChecks.ApplyProcesses(source, rows);
        }
    }
}

public static partial class WinUiChecks
{
    internal static void HiddenProcessBoundRows(ProcessTable table, ListView list)
        => Require(AlignedRealizedRows(table, list) > 0, "Hidden process return has no visible row with seven current cells.");

    internal static string[] HiddenProcessCellTexts(ListView list, ProcessSlot slot)
    {
        var item = list.ContainerFromItem(slot) as ListViewItem;
        var grid = item is not null ? Find<ProcessRowGrid>(item) : null;
        if (grid is null) throw new InvalidOperationException("The isolated hidden-process target has no row template.");
        Require(ProcessRowVisible(list, grid), "The isolated hidden-process target is not visible.");
        var name = grid.Children[0] as ProcessNameCell;
        Require(name is not null && ReferenceEquals(name.Slot, slot) && ReferenceEquals(name.Row, slot.Row),
            "The isolated hidden-process target lost its canonical row.");
        return [name!.NameLabel.Text, .. Enumerable.Range(1, 6).Select(id => (grid.Children[id] as TextBlock)?.Text
            ?? throw new InvalidOperationException("The isolated hidden-process row lost a metric cell."))];
    }
}
