using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private sealed record CpuSetChoice(ProcessCpuSet Cpu)
    {
        public override string ToString() => Loc.F("Skupina {0} · CPU {1} · jádro {2} · třída efektivity {3} · ID {4}", Cpu.Group, Cpu.LogicalProcessor, Cpu.Core, Cpu.Efficiency, Cpu.Id)
            + (!Cpu.Available ? " · " + Loc.T("rezervováno jinému procesu") : (Cpu.Flags & 1) != 0 ? " · " + Loc.T("zaparkováno") : "");
    }

    private async Task<ProcessActionsRequest?> ConfirmCpuSetsAsync(ProcessRow[] rows, ProcessCpuSetsSnapshot snapshot)
    {
        if (!snapshot.Valid) return null;
        var content = new StackPanel { Spacing = 10, MinWidth = 280 };
        content.Children.Add(ActionText(ActionTargetName(rows[0]), 0xe9eef0));
        content.Children.Add(ActionText(ActionExplanation(ProcessActionCommand.CpuSets)));
        content.Children.Add(ActionText(Loc.F("Skupiny: {0} · CPU sets: {1} · nyní výslovně vybráno: {2}",
            snapshot.Processors.Select(cpu => cpu.Group).Distinct().Count(), snapshot.Processors.Count, snapshot.DefaultIds.Count)));
        var reset = new CheckBox { Content = Loc.T("Zrušit výchozí výběr CPU sets"), IsChecked = snapshot.DefaultIds.Count == 0,
            FontFamily = UiTypography.Font, FontSize = 12 };
        content.Children.Add(reset);
        var choices = snapshot.Processors.OrderBy(cpu => cpu.Group).ThenBy(cpu => cpu.LogicalProcessor).ThenBy(cpu => cpu.Id)
            .Select(cpu => new CpuSetChoice(cpu)).ToArray();
        var list = new ListView { ItemsSource = choices, SelectionMode = ListViewSelectionMode.Multiple,
            IsMultiSelectCheckBoxEnabled = true, Height = 230, FontFamily = UiTypography.Font, FontSize = 12 };
        var existing = snapshot.DefaultIds.ToHashSet();
        foreach (var choice in choices) if (existing.Contains(choice.Cpu.Id)) list.SelectedItems.Add(choice);
        AutomationProperties.SetName(list, Loc.T("Výchozí logické procesory procesu včetně čísla skupiny"));
        AutomationProperties.SetAutomationId(list, "process-cpu-sets");
        content.Children.Add(list);
        var selection = ActionText(""); content.Children.Add(selection);
        content.Children.Add(ActionText(Loc.T("Zrušení výběru obnoví plánování bez výchozího omezení CPU sets. Samostatná nastavení vláken ani pevná afinita se tím nemění. Při změně topologie nebo původního výběru se požadavek odmítne a je nutný nový náhled.")));
        var dialog = ActionDialog(Loc.T("Výchozí CPU sets"), content);
        dialog.PrimaryButtonText = Loc.T("Nastavit výchozí výběr"); dialog.CloseButtonText = Loc.T("Zrušit");
        dialog.DefaultButton = ContentDialogButton.Close;
        bool Ready() {
            bool resetting = reset.IsChecked == true;
            list.IsEnabled = !resetting;
            var selected = list.SelectedItems.OfType<CpuSetChoice>().ToArray();
            selection.Text = resetting ? Loc.T("Požadavek: zrušit výchozí výběr CPU sets.")
                : Loc.F("Vybráno: {0} · skupiny: {1}", selected.Length, selected.Select(choice => choice.Cpu.Group).Distinct().Count());
            if (!resetting && selected.Any(choice => !choice.Cpu.Available))
                selection.Text += ". " + Loc.T("Odeberte procesory rezervované jinému procesu.");
            return resetting || selected.Length > 0 && selected.All(choice => choice.Cpu.Available);
        }
        void Refresh() => dialog.IsPrimaryButtonEnabled = !processActionsClosed && ActionSelectionUnchanged(rows) && Ready();
        reset.Checked += (_, _) => Refresh(); reset.Unchecked += (_, _) => Refresh();
        list.SelectionChanged += (_, _) => Refresh();
        dialog.PrimaryButtonClick += (_, args) => { if (!ActionSelectionUnchanged(rows) || !Ready()) args.Cancel = true; };
        processActionInputReady = Ready; processActionDialog = dialog; Refresh();
        var choiceResult = await dialog.ShowAsync(); processActionDialog = null; processActionInputReady = null;
        if (choiceResult != ContentDialogResult.Primary) return null;
        uint[] ids = reset.IsChecked == true ? [] : list.SelectedItems.OfType<CpuSetChoice>().Select(choice => choice.Cpu.Id).Order().ToArray();
        return ActionRequest(ProcessActionCommand.CpuSets, rows) with { CpuSets = new(snapshot.Baseline, Array.AsReadOnly(ids)) };
    }

    private static void AddCpuSetsResults(StackPanel content, ProcessRow row, ProcessActionsBatch result)
    {
        content.Children.Add(ActionText(ActionTargetName(row), 0xe9eef0));
        if (result.Frame != ProcessActionFrame.CpuSets || result.Results.Length != 1) {
            content.Children.Add(ActionText((result.Accepted ? Loc.T("Výsledek není známý. Nastavení se mohlo změnit.")
                : Loc.T("Pracovník požadavek nepřijal; nastavení nebylo změněno.")) + WindowsError(result.Error)));
            return;
        }
        var value = result.Results[0];
        content.Children.Add(ActionText(value.Outcome == ProcessActionOutcome.Applied
            ? Loc.T("Výchozí výběr CPU sets byl potvrzen zpětným čtením.")
            : ActionResultText(value, result.Command)));
        if (result.CpuSets is not { Valid: true } snapshot) return;
        if (snapshot.DefaultIds.Count == 0) content.Children.Add(ActionText(Loc.T("Proces nemá výchozí omezení CPU sets.")));
        else {
            var ids = snapshot.DefaultIds.ToHashSet();
            var groups = snapshot.Processors.Where(cpu => ids.Contains(cpu.Id)).GroupBy(cpu => cpu.Group).OrderBy(group => group.Key);
            foreach (var group in groups)
                content.Children.Add(ActionText(Loc.F("Skupina {0}: {1}", group.Key,
                    string.Join(", ", group.OrderBy(cpu => cpu.LogicalProcessor).Select(cpu => "CPU " + cpu.LogicalProcessor + " (ID " + cpu.Id + ")")))));
        }
        content.Children.Add(ActionText(Loc.T("Jde o načtené výchozí přiřazení. Explicitní volby vláken a pevná afinita mohou jeho použití omezit.")));
    }
}
