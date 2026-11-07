using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    internal void CheckHardwareRefresh()
    {
        bool hadFacts = model.Hardware.TryGetValue("cpu", out var previous);
        bool expanded = cpuSensorDetails.IsExpanded;
        var panel = hardwareSections["cpu"].Panel;
        var cards = hardwareCards.Children.ToArray();
        var choice = cpuSensorChoice;
        var details = cpuSensorDetails;
        var detailsPanel = cpuSensorDetailsRows.Panel;
        try {
            model.Hardware["cpu"] = new() { ["QA měněný údaj"] = "1", ["QA zachovaný údaj"] = "Stejné" };
            RefreshHardware();
            var changed = HardwareQaLine(panel, "QA měněný údaj");
            var kept = HardwareQaLine(panel, "QA zachovaný údaj");
            var reading = (TextBlock)changed.Children[1];
            var modelLine = HardwareQaLine(panel, "Model");
            var status = HardwareQaLine(panel, Loc.T("Stav CPU čidel"));
            cpuSensorDetails.IsExpanded = true;

            model.Hardware["cpu"] = new() { ["QA měněný údaj"] = "2", ["QA zachovaný údaj"] = "Stejné" };
            RefreshHardware();
            HardwareQaRequire(ReferenceEquals(changed, HardwareQaLine(panel, "QA měněný údaj"))
                && ReferenceEquals(reading, changed.Children[1]) && reading.Text == "2"
                && ReferenceEquals(kept, HardwareQaLine(panel, "QA zachovaný údaj")), "A changed hardware value replaced existing rows.");

            model.Hardware["cpu"] = new() { ["QA nový údaj"] = "Nové", ["QA měněný údaj"] = "2", ["QA zachovaný údaj"] = "Stejné" };
            RefreshHardware();
            var added = HardwareQaLine(panel, "QA nový údaj");
            HardwareQaRequire(ReferenceEquals(changed, HardwareQaLine(panel, "QA měněný údaj"))
                && ReferenceEquals(kept, HardwareQaLine(panel, "QA zachovaný údaj")), "Adding a hardware fact replaced neighboring rows.");

            model.Hardware["cpu"] = new() { ["QA zachovaný údaj"] = "Stejné", ["QA nový údaj"] = "Nové" };
            RefreshHardware();
            HardwareQaRequire(!panel.Children.Contains(changed)
                && ReferenceEquals(kept, HardwareQaLine(panel, "QA zachovaný údaj"))
                && ReferenceEquals(added, HardwareQaLine(panel, "QA nový údaj"))
                && panel.Children.IndexOf(kept) < panel.Children.IndexOf(added), "Removing or reordering facts rebuilt unrelated hardware rows.");
            var unchanged = panel.Children.ToArray();
            RefreshHardware();
            HardwareQaRequire(unchanged.SequenceEqual(panel.Children) && ReferenceEquals(modelLine, HardwareQaLine(panel, "Model"))
                && ReferenceEquals(status, HardwareQaLine(panel, Loc.T("Stav CPU čidel"))), "Repeated hardware refresh replaced stable facts.");
            HardwareQaRequire(cards.SequenceEqual(hardwareCards.Children) && ReferenceEquals(panel, hardwareSections["cpu"].Panel)
                && ReferenceEquals(choice, cpuSensorChoice) && ReferenceEquals(details, cpuSensorDetails)
                && ReferenceEquals(detailsPanel, cpuSensorDetailsRows.Panel) && cpuSensorDetails.IsExpanded,
                "Hardware refresh recreated cards, sensor controls or the expanded details panel.");

            var repeated = new HardwareFactRows();
            repeated.Update([new("Stejný název", "První"), new("Stejný název", "Druhý")]);
            var first = repeated.Panel.Children[0]; var second = repeated.Panel.Children[1];
            repeated.Update([new("Stejný název", "Změněné"), new("Stejný název", "Druhý")]);
            HardwareQaRequire(ReferenceEquals(first, repeated.Panel.Children[0]) && ReferenceEquals(second, repeated.Panel.Children[1])
                && ((TextBlock)((Grid)first).Children[1]).Text == Loc.T("Změněné")
                && ((TextBlock)((Grid)second).Children[1]).Text == "Druhý", "Equal hardware labels lost their distinct occurrences.");
        } finally {
            if (hadFacts) model.Hardware["cpu"] = previous!;
            else model.Hardware.Remove("cpu");
            cpuSensorDetails.IsExpanded = expanded;
            RefreshHardware();
        }
        App.Trace("Hardware refresh passed: stableRows=True; changedText=True; addRemoveReorder=True; stableSensorControls=True; duplicateLabels=True");
    }

    private static Grid HardwareQaLine(StackPanel panel, string caption) => panel.Children.OfType<Grid>()
        .Single(line => line.Children[0] is TextBlock text && text.Text == Loc.T(caption));

    private static void HardwareQaRequire(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
