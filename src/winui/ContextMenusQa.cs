using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    internal static unsafe void ContextMenus()
    {
        using var model = new MonitorModel();
        model.SetPaused(true);
        ApplyProcesses(model, Process(62001, 100, 1), Process(62002, 200, 2));
        var a = model.Processes.Single(row => row.Key.Pid == 62001);
        var b = model.Processes.Single(row => row.Key.Pid == 62002);
        var first = new ProcessSlot(a); var second = new ProcessSlot(b);
        var list = new ListView { SelectionMode = ListViewSelectionMode.Extended };
        list.Items.Add(first); list.Items.Add(second); list.SelectedItem = first;
        Require(ReferenceEquals(ContextListSelection.Select(list, second), second) && list.SelectedItems.Count == 1
            && ReferenceEquals(list.SelectedItem, second), "A context request retained the previous row instead of selecting the clicked row.");
        list.SelectedItems.Add(first);
        var selected = list.SelectedItems.ToArray();
        ContextListSelection.Select(list, second);
        Require(list.SelectedItems.Count == 2 && selected.All(list.SelectedItems.Contains), "A context request on a selected row discarded the multiselection.");
        Require(ContextListSelection.FromSource(list, list, keyboard: false) is null && list.SelectedItems.Count == 2,
            "An empty pointer request targeted an old selected row.");
        Require(ReferenceEquals(ContextListSelection.FromSource(list, list, keyboard: true), list.SelectedItem)
            && list.SelectedItems.Count == 2, "A keyboard context request discarded the focused list's current selection.");
        Require(ContextListSelection.Select(list, new ProcessSlot(a)) is null && list.SelectedItems.Count == 2,
            "A foreign row became the context selection.");
        var snapshot = new ProcessContextSnapshot(list);
        Require(snapshot.Matches(model, list), "A current context selection failed its identity guard.");
        second.Assign(a);
        Require(!snapshot.Matches(model, list), "A recycled process slot retained a menu for another row.");
        second.Assign(b); list.SelectedItems.Remove(first);
        Require(!snapshot.Matches(model, list), "A changed multiselection retained the previous context targets.");
        list.SelectedItems.Add(first);
        var current = new ProcessContextSnapshot(list);
        ApplyProcesses(model, Process(62001, 101, 1), Process(62002, 200, 2));
        Require(!current.Matches(model, list), "A PID reused for another process lifetime retained a context action.");
        var solo = new ListView { SelectionMode = ListViewSelectionMode.Extended };
        var live = model.Processes.Single(row => row.Key.Pid == 62002);
        var slot = new ProcessSlot(live); solo.Items.Add(slot); solo.SelectedItem = slot;
        var creation = new ProcessContextSnapshot(solo);
        live.SetCreatedTime(live.Created100ns + 1);
        Require(!creation.Matches(model, solo), "A changed raw creation time retained a previous context action.");
        var service = new ServiceRow("fixture-context-service");
        service.Update(new ServiceInfo { State = 1, Config = new ServiceConfigInfo { Error = 5 } });
        var serviceList = new ListView { SelectionMode = ListViewSelectionMode.Single };
        serviceList.Items.Add(service); serviceList.SelectedItem = service;
        var serviceSnapshot = new ServiceContextSnapshot(service);
        Require(serviceSnapshot.Matches(service, serviceList), "The unchanged service context did not retain its row identity.");
        Require(!serviceSnapshot.Matches(new ServiceRow(service.Name), serviceList), "Another service row with the same name retained an old context action.");
        service.Update(new ServiceInfo { State = 4, Config = new ServiceConfigInfo { Error = 5 } });
        Require(!serviceSnapshot.Matches(service, serviceList), "A service state change retained an old context action.");
        App.Trace("Context selection guards passed: clicked row, multiselect, empty/keyboard list requests, recycled slots, PID reuse, raw creation time and service identity/state; no action invoked");
    }
}
