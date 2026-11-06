using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private static string TreeTargetName(ProcessRow root, ProcessActionTarget target) => target.Key == root.Key
        ? ActionTargetName(root) + Loc.T(" · potvrzený kořen")
        : Loc.F("PID {0} · {1} · generace {2}", target.Key.Pid, target.Key.Kind, target.Key.Generation);

    private static void AddUnknownTreeResults(StackPanel content, ProcessRow root, ProcessTreeActionsPlan? plan, string message)
    {
        content.Children.Add(ActionText(message + " " + Loc.T("Úplný ověřený výsledek chybí. Nepotvrzené cíle mohou být ukončené; zásah se nebude automaticky opakovat.")));
        if (plan is null) {
            content.Children.Add(ActionText(ActionTargetName(root) + Loc.T(" · výsledek není známý. Další cíle se nepodařilo ověřeně převzít."))); return;
        }
        foreach (var node in plan.Nodes) content.Children.Add(ActionText(TreeTargetName(root, node.Target) + "\n" + Loc.T("Výsledek není známý. Akce se mohla provést."), 0xe9eef0));
    }

    private static void AddTreeActionResults(StackPanel content, ProcessRow root, ProcessActionsBatch batch)
    {
        if (batch.Frame != ProcessActionFrame.TreeResults || batch.Tree is not { } plan) {
            if (batch.Accepted) AddUnknownTreeResults(content, root, batch.Tree, Loc.T("Pracovník neposlal úplný výsledek.") + WindowsError(batch.Error));
            else content.Children.Add(ActionText(ActionTargetName(root) + "\n" + Loc.T("Pracovník požadavek nepřijal, zásah nebyl proveden.") + WindowsError(batch.Error)));
            return;
        }
        content.Children.Add(ActionText(Loc.T("Výsledky známých cílů. Průchod není atomický a nedokazuje, že nezůstal žádný souběžně vzniklý potomek.")));
        content.Children.Add(ActionText(Loc.F("Doložených cílů: {0} · průchodů: {1} · možných reziduí v posledním inventáři: {2}", plan.Nodes.Length, plan.Passes, plan.ResidualCount)));
        var limits = new List<string>();
        if (plan.Flags.HasFlag(ProcessTreeActionsFlags.TargetLimit)) limits.Add(Loc.T("limit 256 cílů"));
        if (plan.Flags.HasFlag(ProcessTreeActionsFlags.TimeLimit)) limits.Add(Loc.T("časový limit"));
        if (plan.Flags.HasFlag(ProcessTreeActionsFlags.PassLimit)) limits.Add(Loc.T("limit tří průchodů"));
        if (plan.Flags.HasFlag(ProcessTreeActionsFlags.CensusFailed)) limits.Add(Loc.T("nedostupný inventář"));
        if (plan.Flags.HasFlag(ProcessTreeActionsFlags.ParentLost)) limits.Add(Loc.T("zaniklá nebo neověřitelná vazba rodiče"));
        if (plan.Flags.HasFlag(ProcessTreeActionsFlags.Residual)) limits.Add(Loc.T("další neověřená pozorování potomků"));
        if (limits.Count > 0) content.Children.Add(ActionText(Loc.F("Částečný výsledek: {0}.", string.Join(", ", limits))));
        foreach (var result in batch.Results) content.Children.Add(ActionText(TreeTargetName(root, result.Target)
            + "\n" + ActionResultText(result, ProcessActionCommand.TerminateTree), 0xe9eef0));
    }
}
