using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<ProcessSlot> processSlots = [];
    private readonly Dictionary<ProcessRow, ProcessSlot> slotsByRow = [];
    private bool updatingProcessSlots;
    private double slotsAssignMs, slotsRestoreMs, slotsFocusMs;
    private ProcessRow? SelectedProcess => (processList.SelectedItem as ProcessSlot)?.Row;

    private void ToggleProcessBranch(ProcessRow row)
    {
        if (model.Mode is ProcessMode.Tree or ProcessMode.Applications && row.HasChildren && model.IsCurrent(row)) model.Toggle(row);
    }

    private void SelectProcess(ProcessRow row, bool scroll = true)
    {
        processList.SelectedItems.Clear();
        if (!slotsByRow.TryGetValue(row, out var slot)) return;
        processList.SelectedItem = slot;
        if (scroll) processList.ScrollIntoView(slot, ScrollIntoViewAlignment.Leading);
    }

    private Action PreserveProcessView(MonitorModel source)
    {
        ProcessRow[] selected = [];
        ProcessRow? anchor = null, focused = null, primary = null;
        double remainder = 0;
        ScrollViewer? scroll = null;
        bool bound = false;
        void Replacing()
        {
            bound = ReferenceEquals(processList.ItemsSource, processSlots);
            if (!bound) return;
            selected = processList.SelectedItems.OfType<ProcessSlot>().Select(slot => slot.Row).ToArray();
            primary = SelectedProcess;
            scroll ??= Descendant<ScrollViewer>(processList);
            int index = (int)((scroll?.VerticalOffset ?? 0) / ProcessTable.RowHeight);
            anchor = index > 0 && index < source.VisibleProcesses.Count ? source.VisibleProcesses[index] : null;
            remainder = (scroll?.VerticalOffset ?? 0) % ProcessTable.RowHeight;
            var focus = FocusManager.GetFocusedElement(root.XamlRoot) as DependencyObject;
            while (focus is not null && focus is not ListViewItem) focus = VisualTreeHelper.GetParent(focus);
            focused = ((focus as ListViewItem)?.Content as ProcessSlot)?.Row;
            updatingProcessSlots = true;
        }
        void Replaced()
        {
            if (!bound) return;
            long began = Stopwatch.GetTimestamp();
            processList.SelectedItems.Clear();
            while (processSlots.Count > source.VisibleProcesses.Count) processSlots.RemoveAt(processSlots.Count - 1);
            slotsByRow.Clear();
            for (int i = 0; i < source.VisibleProcesses.Count; ++i) {
                var row = source.VisibleProcesses[i];
                if (i == processSlots.Count) processSlots.Add(new ProcessSlot(row) { Columns = processTable.Columns, ToggleBranch = ToggleProcessBranch });
                else processSlots[i].Assign(row);
                slotsByRow.Add(row, processSlots[i]);
            }
            long assigned = Stopwatch.GetTimestamp();
            if (primary is not null && slotsByRow.TryGetValue(primary, out var main)) processList.SelectedItems.Add(main);
            foreach (var row in selected) if (row != primary && slotsByRow.TryGetValue(row, out var slot)) processList.SelectedItems.Add(slot);
            if (anchor is not null) {
                int index = source.VisibleProcesses.IndexOf(anchor);
                if (index >= 0) scroll?.ChangeView(null, index * ProcessTable.RowHeight + remainder, null, true);
            }
            long restored = Stopwatch.GetTimestamp();
            if (focused is not null && source.VisibleProcesses.Contains(focused)) {
                processList.UpdateLayout();
                (processList.ContainerFromItem(slotsByRow[focused]) as ListViewItem)?.Focus(FocusState.Keyboard);
            }
            slotsAssignMs = Stopwatch.GetElapsedTime(began, assigned).TotalMilliseconds;
            slotsRestoreMs = Stopwatch.GetElapsedTime(assigned, restored).TotalMilliseconds;
            slotsFocusMs = Stopwatch.GetElapsedTime(restored).TotalMilliseconds;
            updatingProcessSlots = false; RefreshSelection();
        }
        source.VisibleProcesses.Replacing += Replacing;
        source.VisibleProcesses.Replaced += Replaced;
        return () => {
            source.VisibleProcesses.Replacing -= Replacing;
            source.VisibleProcesses.Replaced -= Replaced;
        };
    }

    private static T? Descendant<T>(DependencyObject element) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); ++i) {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
}
