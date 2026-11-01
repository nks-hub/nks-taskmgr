using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private sealed record CpuSetChoice(ProcessCpuSet Cpu)
    {
        public override string ToString() => "Skupina " + Cpu.Group + " · CPU " + Cpu.LogicalProcessor + " · jádro " + Cpu.Core
            + " · třída efektivity " + Cpu.Efficiency + " · ID " + Cpu.Id
            + (!Cpu.Available ? " · rezervováno jinému procesu" : (Cpu.Flags & 1) != 0 ? " · zaparkováno" : "");
    }

    private async Task<ProcessActionsRequest?> ConfirmCpuSetsAsync(ProcessRow[] rows, ProcessCpuSetsSnapshot snapshot)
    {
        if (!snapshot.Valid) return null;
        var content = new StackPanel { Spacing = 10, MinWidth = 280 };
        content.Children.Add(ActionText(ActionTargetName(rows[0]), 0xe9eef0));
        content.Children.Add(ActionText(ActionExplanation(ProcessActionCommand.CpuSets)));
        content.Children.Add(ActionText("Skupiny: " + snapshot.Processors.Select(cpu => cpu.Group).Distinct().Count()
            + " · CPU sets: " + snapshot.Processors.Count + " · nyní výslovně vybráno: " + snapshot.DefaultIds.Count));
        var reset = new CheckBox { Content = "Zrušit výchozí výběr CPU sets", IsChecked = snapshot.DefaultIds.Count == 0,
            FontFamily = UiTypography.Font, FontSize = 12 };
        content.Children.Add(reset);
        var choices = snapshot.Processors.OrderBy(cpu => cpu.Group).ThenBy(cpu => cpu.LogicalProcessor).ThenBy(cpu => cpu.Id)
            .Select(cpu => new CpuSetChoice(cpu)).ToArray();
        var list = new ListView { ItemsSource = choices, SelectionMode = ListViewSelectionMode.Multiple,
            IsMultiSelectCheckBoxEnabled = true, Height = 230, FontFamily = UiTypography.Font, FontSize = 12 };
        var existing = snapshot.DefaultIds.ToHashSet();
        foreach (var choice in choices) if (existing.Contains(choice.Cpu.Id)) list.SelectedItems.Add(choice);
        AutomationProperties.SetName(list, "Výchozí logické procesory procesu včetně čísla skupiny");
        AutomationProperties.SetAutomationId(list, "process-cpu-sets");
        content.Children.Add(list);
        var selection = ActionText(""); content.Children.Add(selection);
        content.Children.Add(ActionText("Zrušení výběru obnoví plánování bez výchozího omezení CPU sets. Samostatná nastavení vláken ani pevná afinita se tím nemění. Při změně topologie nebo původního výběru se požadavek odmítne a je nutný nový náhled."));
        var dialog = ActionDialog("Výchozí CPU sets", content);
        dialog.PrimaryButtonText = "Nastavit výchozí výběr"; dialog.CloseButtonText = "Zrušit";
        dialog.DefaultButton = ContentDialogButton.Close;
        bool Ready() {
            bool resetting = reset.IsChecked == true;
            list.IsEnabled = !resetting;
            var selected = list.SelectedItems.OfType<CpuSetChoice>().ToArray();
            selection.Text = resetting ? "Požadavek: zrušit výchozí výběr CPU sets."
                : "Vybráno: " + selected.Length + " · skupiny: " + selected.Select(choice => choice.Cpu.Group).Distinct().Count();
            if (!resetting && selected.Any(choice => !choice.Cpu.Available))
                selection.Text += ". Odeberte procesory rezervované jinému procesu.";
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
            content.Children.Add(ActionText((result.Accepted ? "Výsledek není známý. Nastavení se mohlo změnit."
                : "Pracovník požadavek nepřijal; nastavení nebylo změněno.") + WindowsError(result.Error)));
            return;
        }
        var value = result.Results[0];
        content.Children.Add(ActionText(value.Outcome == ProcessActionOutcome.Applied
            ? "Výchozí výběr CPU sets byl potvrzen zpětným čtením."
            : ActionResultText(value, result.Command)));
        if (result.CpuSets is not { Valid: true } snapshot) return;
        if (snapshot.DefaultIds.Count == 0) content.Children.Add(ActionText("Proces nemá výchozí omezení CPU sets."));
        else {
            var ids = snapshot.DefaultIds.ToHashSet();
            var groups = snapshot.Processors.Where(cpu => ids.Contains(cpu.Id)).GroupBy(cpu => cpu.Group).OrderBy(group => group.Key);
            foreach (var group in groups)
                content.Children.Add(ActionText("Skupina " + group.Key + ": "
                    + string.Join(", ", group.OrderBy(cpu => cpu.LogicalProcessor).Select(cpu => "CPU " + cpu.LogicalProcessor + " (ID " + cpu.Id + ")"))));
        }
        content.Children.Add(ActionText("Jde o načtené výchozí přiřazení. Explicitní volby vláken a pevná afinita mohou jeho použití omezit."));
    }
}
