using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private async Task CheckProcessBranchUiAsync()
    {
        var originalView = view;
        var originalMode = model.Mode;
        var originalValueMode = model.TableValueMode;
        string originalQuery = processQuery;
        bool originalPause = model.Paused;
        var originalPrimary = SelectedProcess?.Key;
        var originalSelection = processList.SelectedItems.OfType<ProcessSlot>().Select(slot => slot.Row.Key).ToArray();
        ProcessRow? parent = null;
        bool originalCollapse = false;
        processBranchCheckRunning = true;
        model.SetPaused(true);

        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        async Task Rendered()
        {
            await NextRenderedFrame(); await NextRenderedFrame();
            root.UpdateLayout(); processTable.UpdateLayout(); processList.UpdateLayout();
        }
        async Task Search(string value)
        {
            if (search.Text == value) return;
            var changed = new TaskCompletionSource();
            void Changed(object sender, TextChangedEventArgs args) { if (search.Text == value) changed.TrySetResult(); }
            search.TextChanged += Changed;
            try { search.Text = value; await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { search.TextChanged -= Changed; }
        }

        try {
            ShowView(DesktopView.Processes);
            await Search("");
            model.SetTableValueMode(ProcessTableValueMode.Own);
            model.SetMode(ProcessMode.Tree); RefreshProcesses();
            await Rendered();
            Check(processValueMode.Visibility == Visibility.Visible && processValueMode.SelectedIndex == 0,
                "The process tree did not expose the own-values choice.");
            parent = model.VisibleProcesses.FirstOrDefault(row => row.Key.Pid == Environment.ProcessId && row.HasChildren)
                ?? model.VisibleProcesses.FirstOrDefault(row => row.HasChildren && !row.Ended && !row.Stale);
            Check(parent is not null, "The live process tree has no verified branch for its value-mode check.");
            var current = parent!;
            originalCollapse = current.Collapsed;
            var cpu = current.Cpu; var read = current.ReadIo; var write = current.WriteIo;
            ulong working = current.WorkingBytes, privateBytes = current.PrivateBytes;
            SelectProcess(current);
            await Rendered();
            WinUiChecks.ProcessBranchTable(processTable, processList, current, false);

            processValueMode.SelectedIndex = 1;
            await Rendered();
            Check(model.TableValueMode == ProcessTableValueMode.ProcessWithDescendants && current.BranchTotal.Members > 1,
                "The production value-mode control did not select verified branch totals.");
            Check(current.TableWorkingText != current.WorkingText,
                "The live parent and branch memory did not differ in the production value-mode check.");
            string[] totals = [current.TableCpuText, current.TableWorkingText, current.TablePrivateText,
                current.TableReadText, current.TableWriteText, current.TableMetricsDescription];
            WinUiChecks.ProcessBranchTable(processTable, processList, current, true);

            model.Toggle(current); RefreshProcesses();
            await Rendered();
            Check(totals.SequenceEqual([current.TableCpuText, current.TableWorkingText, current.TablePrivateText,
                current.TableReadText, current.TableWriteText, current.TableMetricsDescription]),
                "Collapsing the production tree changed its branch totals.");
            model.Toggle(current); RefreshProcesses();
            await Search(current.PidText);
            await Rendered();
            Check(model.VisibleProcesses.Contains(current) && totals.SequenceEqual([current.TableCpuText,
                current.TableWorkingText, current.TablePrivateText, current.TableReadText, current.TableWriteText,
                current.TableMetricsDescription]), "Searching the production tree changed its branch totals.");
            WinUiChecks.ProcessBranchTable(processTable, processList, current, true);

            model.SetMode(ProcessMode.All); RefreshProcesses();
            await Rendered();
            Check(processValueMode.Visibility == Visibility.Collapsed
                && model.TableValueMode == ProcessTableValueMode.ProcessWithDescendants
                && current.TableWorkingText == current.WorkingText,
                "Leaving Tree lost its preference or displayed a branch total in the flat process list.");
            model.SetMode(ProcessMode.Tree); RefreshProcesses();
            await Rendered();
            Check(processValueMode.Visibility == Visibility.Visible && processValueMode.SelectedIndex == 1,
                "Returning to Tree lost its value-mode choice.");
            processValueMode.SelectedIndex = 0;
            await Rendered();
            WinUiChecks.ProcessBranchTable(processTable, processList, current, false);
            Check(current.Cpu.Equals(cpu) && current.ReadIo.Equals(read) && current.WriteIo.Equals(write)
                && current.WorkingBytes == working && current.PrivateBytes == privateBytes,
                "Switching table totals changed the parent's own metrics.");
            await CheckHiddenProcessUiAsync();
            App.Trace("Process branch UI passed: production choice, own/branch bindings, geometry, collapse/search, flat view and retained preference");
        } finally {
            if (parent is not null && parent.Collapsed != originalCollapse) model.Toggle(parent);
            model.SetTableValueMode(originalValueMode);
            model.SetMode(originalMode);
            processQuery = originalQuery;
            search.Text = originalQuery; model.SetQuery(originalQuery);
            ShowView(originalView);
            processList.SelectedItems.Clear();
            var keys = originalPrimary is { } primary ? originalSelection.Prepend(primary).Distinct() : originalSelection;
            foreach (var key in keys)
                if (model.VisibleProcesses.FirstOrDefault(row => row.Key == key) is { } row && slotsByRow.TryGetValue(row, out var slot))
                    processList.SelectedItems.Add(slot);
            model.SetPaused(originalPause);
            processBranchCheckRunning = false;
        }
    }
}

public static partial class WinUiChecks
{
    internal static void ProcessBranchTable(ProcessTable table, ListView list, ProcessRow expected, bool branch)
    {
        var slot = list.Items.OfType<ProcessSlot>().Single(item => ReferenceEquals(item.Row, expected));
        var item = list.ContainerFromItem(slot) as ListViewItem
            ?? throw new InvalidOperationException("The branch-values target was not realized.");
        var grid = Find<ProcessRowGrid>(item)
            ?? throw new InvalidOperationException("The branch-values target has no production row template.");
        Require(ProcessRowVisible(list, grid), "The branch-values target is outside the viewport.");
        Require(AlignedRealizedRows(table, list) > 0, "Branch values have no aligned visible production row.");
        string description = expected.TableMetricsDescription;
        var tooltip = ToolTipService.GetToolTip(grid);
        string? tooltipText = tooltip is ToolTip control ? control.Content as string : tooltip as string;
        Require(description.Length > 0 && tooltipText == description && AutomationProperties.GetHelpText(grid) == description,
            "The production row lost its branch membership and metric-availability description.");
        var header = ((Grid)table.Header.Children[2]).Children.OfType<Button>().Single();
        Require(AutomationProperties.GetName(header).Contains(branch ? "proces s potomky" : "vlastní hodnoty", StringComparison.Ordinal),
            "The accessible metric heading does not explain the displayed value scope.");
        if (!branch) Require(expected.TableCpuText == expected.CpuText && expected.TableWorkingText == expected.WorkingText
            && expected.TablePrivateText == expected.PrivateText && expected.TableReadText == expected.ReadText
            && expected.TableWriteText == expected.WriteText, "Own-values mode changed the meaning of a metric cell.");
    }
}
