using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<ProcessSlot> processSlots = [];
    private readonly Dictionary<ProcessRow, ProcessSlot> slotsByRow = [];
    private bool updatingProcessSlots;
    private ProcessRow? SelectedProcess => (processList.SelectedItem as ProcessSlot)?.Row;

    private void SelectProcess(ProcessRow row, bool scroll = true)
    {
        processList.SelectedItems.Clear();
        if (!slotsByRow.TryGetValue(row, out var slot)) return;
        processList.SelectedItem = slot;
        if (scroll) processList.ScrollIntoView(slot, ScrollIntoViewAlignment.Leading);
    }

    private void PreserveProcessView()
    {
        ProcessRow[] selected = [];
        ProcessRow? anchor = null, focused = null, primary = null;
        double remainder = 0;
        ScrollViewer? scroll = null;
        bool bound = false;
        model.VisibleProcesses.Replacing += () => {
            bound = ReferenceEquals(processList.ItemsSource, processSlots);
            if (!bound) return;
            selected = processList.SelectedItems.OfType<ProcessSlot>().Select(slot => slot.Row).ToArray();
            primary = SelectedProcess;
            scroll ??= Descendant<ScrollViewer>(processList);
            int index = (int)((scroll?.VerticalOffset ?? 0) / 34);
            anchor = index > 0 && index < model.VisibleProcesses.Count ? model.VisibleProcesses[index] : null;
            remainder = (scroll?.VerticalOffset ?? 0) % 34;
            var focus = FocusManager.GetFocusedElement(root.XamlRoot) as DependencyObject;
            while (focus is not null && focus is not ListViewItem) focus = VisualTreeHelper.GetParent(focus);
            focused = ((focus as ListViewItem)?.Content as ProcessSlot)?.Row;
            updatingProcessSlots = true;
        };
        model.VisibleProcesses.Replaced += () => {
            if (!bound) return;
            processList.SelectedItems.Clear();
            while (processSlots.Count > model.VisibleProcesses.Count) processSlots.RemoveAt(processSlots.Count - 1);
            slotsByRow.Clear();
            for (int i = 0; i < model.VisibleProcesses.Count; ++i) {
                var row = model.VisibleProcesses[i];
                if (i == processSlots.Count) processSlots.Add(new ProcessSlot(row)); else processSlots[i].Assign(row);
                slotsByRow.Add(row, processSlots[i]);
            }
            if (primary is not null && slotsByRow.TryGetValue(primary, out var main)) processList.SelectedItems.Add(main);
            foreach (var row in selected) if (row != primary && slotsByRow.TryGetValue(row, out var slot)) processList.SelectedItems.Add(slot);
            if (anchor is not null) {
                int index = model.VisibleProcesses.IndexOf(anchor);
                if (index >= 0) scroll?.ChangeView(null, index * 34 + remainder, null, true);
            }
            if (focused is not null && model.VisibleProcesses.Contains(focused)) {
                processList.UpdateLayout();
                (processList.ContainerFromItem(slotsByRow[focused]) as ListViewItem)?.Focus(FocusState.Keyboard);
            }
            updatingProcessSlots = false; RefreshSelection();
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
