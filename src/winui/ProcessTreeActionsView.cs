using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private static string TreeTargetName(ProcessRow root, ProcessActionTarget target) => target.Key == root.Key
        ? ActionTargetName(root) + " · potvrzený kořen"
        : "PID " + target.Key.Pid + " · " + target.Key.Kind + " · generace " + target.Key.Generation;

    private static void AddUnknownTreeResults(StackPanel content, ProcessRow root, ProcessTreeActionsPlan? plan, string message)
    {
        content.Children.Add(ActionText(message + " Úplný ověřený výsledek chybí. Nepotvrzené cíle mohou být ukončené; zásah se nebude automaticky opakovat."));
        if (plan is null) {
            content.Children.Add(ActionText(ActionTargetName(root) + " · výsledek není známý. Další cíle se nepodařilo ověřeně převzít.")); return;
        }
        foreach (var node in plan.Nodes) content.Children.Add(ActionText(TreeTargetName(root, node.Target) + "\nVýsledek není známý. Akce se mohla provést.", 0xe9eef0));
    }

    private static void AddTreeActionResults(StackPanel content, ProcessRow root, ProcessActionsBatch batch)
    {
        if (batch.Frame != ProcessActionFrame.TreeResults || batch.Tree is not { } plan) {
            if (batch.Accepted) AddUnknownTreeResults(content, root, batch.Tree, "Pracovník neposlal úplný výsledek." + WindowsError(batch.Error));
            else content.Children.Add(ActionText(ActionTargetName(root) + "\nPracovník požadavek nepřijal, zásah nebyl proveden." + WindowsError(batch.Error)));
            return;
        }
        content.Children.Add(ActionText("Výsledky známých cílů. Průchod není atomický a nedokazuje, že nezůstal žádný souběžně vzniklý potomek."));
        content.Children.Add(ActionText("Doložených cílů: " + plan.Nodes.Length + " · průchodů: " + plan.Passes + " · možných reziduí v posledním inventáři: " + plan.ResidualCount));
        var limits = new List<string>();
        if (plan.Flags.HasFlag(ProcessTreeActionsFlags.TargetLimit)) limits.Add("limit 256 cílů");
        if (plan.Flags.HasFlag(ProcessTreeActionsFlags.TimeLimit)) limits.Add("časový limit");
        if (plan.Flags.HasFlag(ProcessTreeActionsFlags.PassLimit)) limits.Add("limit tří průchodů");
        if (plan.Flags.HasFlag(ProcessTreeActionsFlags.CensusFailed)) limits.Add("nedostupný inventář");
        if (plan.Flags.HasFlag(ProcessTreeActionsFlags.ParentLost)) limits.Add("zaniklá nebo neověřitelná vazba rodiče");
        if (plan.Flags.HasFlag(ProcessTreeActionsFlags.Residual)) limits.Add("další neověřená pozorování potomků");
        if (limits.Count > 0) content.Children.Add(ActionText("Částečný výsledek: " + string.Join(", ", limits) + "."));
        foreach (var result in batch.Results) content.Children.Add(ActionText(TreeTargetName(root, result.Target)
            + "\n" + ActionResultText(result, ProcessActionCommand.TerminateTree), 0xe9eef0));
    }
}
