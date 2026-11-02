using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static class ContextListSelection
{
    internal static object? Select(ListView list, object? item)
    {
        if (item is null || !list.Items.Contains(item)) return null;
        if (!list.SelectedItems.Contains(item)) {
            list.SelectedItems.Clear();
            list.SelectedItem = item;
        }
        return item;
    }

    internal static object? FromSource(ListView list, DependencyObject? source, bool keyboard)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node)) {
            if (ReferenceEquals(node, list)) return keyboard ? Select(list, list.SelectedItem) : null;
            if (node is not ListViewItem container) continue;
            return Select(list, list.ItemFromContainer(container));
        }
        return null;
    }
}

internal sealed class ProcessContextSnapshot(ListView list)
{
    private sealed record Target(ProcessRow Row, ProcessKey Key, ulong Created);
    private readonly Target[] targets = list.SelectedItems.OfType<ProcessSlot>()
        .Select(slot => new Target(slot.Row, slot.Row.Key, slot.Row.Created100ns)).ToArray();
    internal int Count => targets.Length;
    internal ProcessRow? Single => targets.Length == 1 ? targets[0].Row : null;
    internal bool Matches(MonitorModel model, ListView selection)
    {
        if (targets.Length == 0 || selection.SelectedItems.Count != targets.Length) return false;
        var rows = selection.SelectedItems.OfType<ProcessSlot>().Select(slot => slot.Row).ToArray();
        return rows.Length == targets.Length && targets.All(target => model.IsCurrent(target.Row)
            && target.Row.Key == target.Key && target.Row.Created100ns == target.Created && rows.Contains(target.Row));
    }
}

internal sealed class ServiceContextSnapshot(ServiceRow row)
{
    private readonly string name = row.Name, title = row.Title;
    private readonly uint state = row.State, pid = row.Pid;
    private readonly ServiceConfiguration config = row.Config;
    internal ServiceRow Row => row;
    internal bool Matches(ServiceRow? current, ListView selection) => ReferenceEquals(current, row)
        && ReferenceEquals(selection.SelectedItem, row)
        && row.Name == name && row.Title == title && row.State == state && row.Pid == pid && row.Config == config;
}

public sealed partial class MainWindow
{
    private MenuFlyout? activeContextMenu;

    private void InitializeContextMenus()
    {
        processList.ContextRequested += ProcessContextRequested;
        serviceList.ContextRequested += ServiceContextRequested;
        resourceList.ContextRequested += ResourceContextRequested;
        performancePage.ContextRequested += ChartContextRequested;
        Closed += (_, _) => { activeContextMenu?.Hide(); activeContextMenu = null; };
    }

    private static T? ContextAncestor<T>(DependencyObject? source, DependencyObject boundary) where T : DependencyObject
    {
        for (var node = source; node is not null && !ReferenceEquals(node, boundary); node = VisualTreeHelper.GetParent(node))
            if (node is T value) return value;
        return null;
    }

    private static object? ContextRow(ListView list, ContextRequestedEventArgs args)
    {
        bool pointer = args.TryGetPosition(list, out _);
        var source = pointer ? args.OriginalSource as DependencyObject
            : FocusManager.GetFocusedElement(list.XamlRoot) as DependencyObject;
        return ContextListSelection.FromSource(list, source, !pointer);
    }

    private void ShowContextMenu(MenuFlyout menu, FrameworkElement owner, ContextRequestedEventArgs args)
    {
        activeContextMenu?.Hide(); activeContextMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(activeContextMenu, menu)) activeContextMenu = null; };
        if (args.TryGetPosition(owner, out var point)) menu.ShowAt(owner, new FlyoutShowOptions { Position = point });
        else if (owner is ListView list) {
            var focused = FocusManager.GetFocusedElement(list.XamlRoot) as DependencyObject;
            var container = ContextAncestor<ListViewItem>(focused, list);
            menu.ShowAt(container ?? list.ContainerFromItem(list.SelectedItem) as FrameworkElement ?? list);
        }
        else menu.ShowAt(owner);
        args.Handled = true;
    }

    private static MenuFlyoutItem ContextItem(string text, string id, bool enabled, Action invoke)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        AutomationProperties.SetAutomationId(item, id);
        item.Click += (_, _) => { if (item.IsEnabled) invoke(); };
        return item;
    }

    private static void InvokeContextButton(Button button)
    {
        if (button.IsEnabled && button.Visibility == Visibility.Visible) new ButtonAutomationPeer(button).Invoke();
    }

    private void ProcessContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.Handled || view != DesktopView.Processes) return;
        args.Handled = true;
        activeContextMenu?.Hide();
        if (ContextRow(processList, args) is not ProcessSlot) return;
        var snapshot = new ProcessContextSnapshot(processList);
        bool Current() => view == DesktopView.Processes && snapshot.Matches(model, processList);
        bool ActionsAvailable() { RefreshProcessActions(); return Current() && processActionsButton.IsEnabled; }
        var menu = new MenuFlyout();
        var single = snapshot.Single;
        menu.Items.Add(ContextItem("Podrobnosti procesu…", "context-process-details", single is not null && CanReadDetails(single), () => {
            if (Current() && snapshot.Single is { } row && CanReadDetails(row)) InvokeContextButton(processDetailsButton);
        }));
        RefreshProcessDump();
        menu.Items.Add(ContextItem(captureDumpCommand.Text, "context-process-dump", snapshot.Count == 1 && captureDumpCommand.IsEnabled,
            async () => { if (Current()) { RefreshProcessDump(); if (captureDumpCommand.IsEnabled) await CreateProcessDumpAsync(); } }));
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var command in new[] { ProcessActionCommand.CloseWindows, ProcessActionCommand.Terminate, ProcessActionCommand.TerminateTree,
            ProcessActionCommand.Suspend, ProcessActionCommand.Resume, ProcessActionCommand.Priority, ProcessActionCommand.Affinity, ProcessActionCommand.CpuSets }) {
            bool singleOnly = command is ProcessActionCommand.TerminateTree or ProcessActionCommand.Affinity or ProcessActionCommand.CpuSets;
            menu.Items.Add(ContextItem(ActionTitle(command) + "…", "context-process-" + command.ToString().ToLowerInvariant(),
                ActionsAvailable() && (!singleOnly || snapshot.Count == 1), async () => {
                    if (ActionsAvailable() && (!singleOnly || snapshot.Count == 1)) await StartProcessActionAsync(command);
                }));
        }
        ShowContextMenu(menu, processList, args);
    }

    private void ServiceContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.Handled || view != DesktopView.Services) return;
        args.Handled = true;
        activeContextMenu?.Hide();
        if (ContextRow(serviceList, args) is not ServiceRow row) return;
        var snapshot = new ServiceContextSnapshot(row);
        bool Current() => view == DesktopView.Services && snapshot.Matches(servicesModel?.Find(row.Name), serviceList);
        bool Available(ServiceAction action) {
            RefreshServiceActions();
            return Current() && serviceActionCommands.TryGetValue(action, out var original) && original.IsEnabled;
        }
        var menu = new MenuFlyout();
        foreach (var action in new[] { ServiceAction.Start, ServiceAction.Stop, ServiceAction.Restart })
            menu.Items.Add(ContextItem(ServiceActionTitle(action) + "…", "context-service-" + action.ToString().ToLowerInvariant(), Available(action),
                async () => { if (Available(action)) await StartServiceActionAsync(action); }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(ContextItem("Závislosti služby", "context-service-dependencies", Current(), () => {
            if (Current()) InvokeContextButton(serviceDependenciesTab);
        }));
        var host = servicesModel?.Host(model);
        var hostKey = host?.Key;
        menu.Items.Add(ContextItem("Otevřít hostitelský proces", "context-service-host", Current() && serviceHost.IsEnabled && host is not null, () => {
            if (Current() && host is not null && host.Key == hostKey && model.IsCurrent(host)
                && ReferenceEquals(servicesModel?.Host(model), host)) InvokeContextButton(serviceHost);
        }));
        ShowContextMenu(menu, serviceList, args);
    }

    private bool ResourceContextCurrent(ResourceRow row) => view == DesktopView.Performance
        && ReferenceEquals(resourceList.SelectedItem, row) && ReferenceEquals(selectedResource, row)
        && model.AllResources.Any(current => ReferenceEquals(current, row));

    private void ResourceContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.Handled || view != DesktopView.Performance) return;
        args.Handled = true;
        activeContextMenu?.Hide();
        if (ContextRow(resourceList, args) is not ResourceRow row) return;
        ShowContextMenu(PerformanceContextMenu(row), resourceList, args);
    }

    private void ChartContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.Handled || view != DesktopView.Performance || selectedResource is not { } resource) return;
        var source = args.TryGetPosition(performancePage, out _) ? args.OriginalSource as DependencyObject
            : FocusManager.GetFocusedElement(root.XamlRoot) as DependencyObject;
        if (ContextAncestor<ChartView>(source, performancePage) is not { } plot || !ResourceContextCurrent(resource)) return;
        ShowContextMenu(PerformanceContextMenu(resource), plot, args);
    }

    private MenuFlyout PerformanceContextMenu(ResourceRow resource)
    {
        bool Current() => ResourceContextCurrent(resource);
        var menu = new MenuFlyout();
        bool paused = model.Paused; uint refresh = model.Interval;
        menu.Items.Add(ContextItem(paused ? "Pokračovat ve sběru" : "Pozastavit sběr", "context-performance-pause", pause.IsEnabled, () => {
            if (Current() && model.Paused == paused) InvokeContextButton(pause);
        }));
        menu.Items.Add(ContextItem(refresh == 1000 ? "Obnovovat každých 250 ms" : "Obnovovat každou 1 s", "context-performance-refresh", interval.IsEnabled, () => {
            if (Current() && model.Interval == refresh) InvokeContextButton(interval);
        }));
        menu.Items.Add(new MenuFlyoutSeparator());
        var range = new MenuFlyoutSubItem { Text = "Časové okno" };
        foreach (int seconds in new[] { 60, 600 }) {
            var item = new ToggleMenuFlyoutItem { Text = seconds == 60 ? "60 sekund" : "10 minut", IsChecked = rangeSeconds == seconds };
            AutomationProperties.SetAutomationId(item, "context-performance-range-" + seconds);
            item.Click += (_, _) => { if (Current()) { rangeSeconds = seconds; RefreshPerformance(); } };
            range.Items.Add(item);
        }
        menu.Items.Add(range);
        if (resource.Kind == ResourceKind.Cpu) {
            var modes = new MenuFlyoutSubItem { Text = "Zobrazení procesoru" };
            for (int index = 0; index < cpuButtons.Count; ++index) {
                int mode = index;
                var item = new ToggleMenuFlyoutItem { Text = cpuButtons[index].Content?.ToString() ?? "", IsChecked = cpuMode == index };
                AutomationProperties.SetAutomationId(item, "context-performance-cpu-" + mode);
                item.Click += (_, _) => { if (Current()) { cpuMode = mode; RefreshPerformance(); } };
                modes.Items.Add(item);
            }
            menu.Items.Add(modes);
        }
        if (scaleChoice.Visibility == Visibility.Visible) {
            var scale = new ToggleMenuFlyoutItem { Text = "Automatické měřítko", IsChecked = scaleChoice.IsChecked == true, IsEnabled = scaleChoice.IsEnabled };
            AutomationProperties.SetAutomationId(scale, "context-performance-scale");
            scale.Click += (_, _) => { if (Current() && scaleChoice.IsEnabled) { scaleChoice.IsChecked = scale.IsChecked; RefreshPerformance(); } };
            menu.Items.Add(scale);
        }
        var timeline = performanceTimeline.State;
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(ContextItem("Procesy ve vybraném čase…", "context-performance-history", historicalProcessesButton.IsEnabled, () => {
            if (Current() && performanceTimeline.State == timeline) InvokeContextButton(historicalProcessesButton);
        }));
        menu.Items.Add(ContextItem("Zrušit výběr času", "context-performance-clear-time", timeline.CursorTimeNs is not null || timeline.RangeAnchorNs is not null, () => {
            if (Current() && performanceTimeline.State == timeline) performanceTimeline.Clear();
        }));
        return menu;
    }
}
