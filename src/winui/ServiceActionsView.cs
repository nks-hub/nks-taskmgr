using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Button serviceActionsButton = Button(Loc.T("Akce"));
    private readonly ServiceActionsClient serviceActionsClient = new();
    private readonly Dictionary<ServiceAction, MenuFlyoutItem> serviceActionCommands = [];
    private CancellationTokenSource? serviceActionOperation;
    private ContentDialog? serviceActionDialog;
    private TaskCompletionSource<uint[]?>? serviceActionConfirmation;
    private Func<bool>? serviceActionReady;
    private ServiceRow? serviceActionTarget;
    private ServiceConfiguration? serviceActionOriginalConfig;
    private string serviceActionOriginalTitle = "";
    private string serviceActionCancellationReason = "";
    private uint serviceActionOriginalState;
    private bool serviceActionBusy, serviceActionApproved, serviceActionsClosed;

    private void ConfigureServiceActions()
    {
        serviceActionsButton.Height = 29; serviceActionsButton.IsEnabled = false;
        AutomationProperties.SetName(serviceActionsButton, Loc.T("Spustit, zastavit nebo restartovat vybranou službu"));
        AutomationProperties.SetAutomationId(serviceActionsButton, "service-actions");
        var menu = new MenuFlyout();
        foreach (var action in new[] { ServiceAction.Start, ServiceAction.Stop, ServiceAction.Restart }) {
            var item = new MenuFlyoutItem { Text = ServiceActionTitle(action) + "…" };
            item.Click += async (_, _) => await StartServiceActionAsync(action);
            serviceActionCommands.Add(action, item); menu.Items.Add(item);
        }
        serviceActionsButton.Flyout = menu;
        Closed += (_, _) => {
            serviceActionsClosed = true; serviceActionOperation?.Cancel(); serviceActionConfirmation?.TrySetResult(null);
            serviceActionDialog?.Hide(); serviceActionsClient.Dispose();
        };
    }

    private bool CanControlService(ServiceRow row) => servicesModel is { Time: > 0, Stale: false, Paused: false, Error.Length: 0 }
        && ReferenceEquals(servicesModel.Find(row.Name), row) && ReferenceEquals(serviceList.SelectedItem, row) && row.Config.Error == 0;
    private bool ServiceActionSelectionCurrent() => serviceActionTarget is { } row && CanControlService(row)
        && row.Config == serviceActionOriginalConfig && row.Title == serviceActionOriginalTitle && row.State == serviceActionOriginalState;
    private void RefreshServiceActions()
    {
        var row = serviceList.SelectedItem as ServiceRow;
        bool available = !serviceActionBusy && !serviceActionsClient.IsBusy && row is not null && CanControlService(row);
        serviceActionsButton.IsEnabled = available && row!.State is 1 or 4 or 7;
        foreach (var (action, item) in serviceActionCommands) item.IsEnabled = available
            && (action == ServiceAction.Start ? row!.State == 1 : row!.State is 4 or 7);
        ToolTipService.SetToolTip(serviceActionsButton, serviceActionsClient.IsBusy ? Loc.T("Akce služeb nebo ukončování jejího pracovníka ještě probíhá.")
            : Loc.T("Vyberte živou službu s dostupnou konfigurací. Windows ověří oprávnění bez automatického zvýšení práv."));
        if (!serviceActionBusy || serviceActionApproved) return;
        bool current = ServiceActionSelectionCurrent();
        if (!current) {
            serviceActionCancellationReason = Loc.T("Výběr, stav nebo konfigurace služby se před schválením změnily. Vyberte znovu aktuální službu.");
            serviceActionOperation?.Cancel(); serviceActionConfirmation?.TrySetResult(null);
        }
        if (serviceActionReady is { } ready && serviceActionDialog is { } dialog) dialog.IsPrimaryButtonEnabled = current && ready();
    }

    private async Task StartServiceActionAsync(ServiceAction action)
    {
        if (serviceActionBusy || serviceActionsClosed || serviceActionsClient.IsBusy || serviceList.SelectedItem is not ServiceRow row || !CanControlService(row)) return;
        if (action == ServiceAction.Start ? row.State != 1 : row.State is not (4 or 7)) return;
        serviceActionBusy = true; serviceActionApproved = false; serviceActionTarget = row;
        serviceActionCancellationReason = "";
        serviceActionOriginalConfig = row.Config; serviceActionOriginalTitle = row.Title; serviceActionOriginalState = row.State;
        using var operation = new CancellationTokenSource(); serviceActionOperation = operation; RefreshServiceActions();
        var body = new StackPanel { Spacing = 12, MinWidth = 280 };
        body.Children.Add(ServiceActionText(Loc.T("Načítám plán správce služeb Windows…")));
        var dialog = ServiceActionDialog(ServiceActionTitle(action), body); serviceActionDialog = dialog; dialog.CloseButtonText = Loc.T("Zrušit");
        bool running = true; Task? work = null; ServiceActionPlan? plan = null;
        var progressLabels = new Dictionary<uint, TextBlock>();
        void RenderProgress(ServiceActionItem[] items) {
            if (!running || serviceActionsClosed || plan is null || !serviceActionApproved) return;
            foreach (var item in items) if (progressLabels.TryGetValue(item.Id, out var label)) label.Text = ServiceActionItemText(item);
        }
        var progress = new Progress<ServiceActionItem[]>(items => DispatcherQueue.TryEnqueue(() => RenderProgress(items)));
        async Task Run() {
            try {
                var result = await serviceActionsClient.ExecuteAsync(action, row.Name, async (proposed, cancellation) => {
                    plan = proposed;
                    if (!ServiceActionSelectionCurrent() || !ServicePlanMatchesRow(proposed, row)) {
                        serviceActionCancellationReason = Loc.T("Plán už neodpovídá výběru, stavu nebo konfiguraci služby. Po obnovení seznamu jej vytvořte znovu."); return null;
                    }
                    var approved = await ConfirmServiceActionAsync(proposed, dialog, body, cancellation);
                    if (approved is null) return null;
                    body.Children.Clear();
                    body.Children.Add(ServiceActionText(Loc.T("Provádím schválený plán. Zrušení čekání nevrátí již přijaté požadavky správce služeb Windows.")));
                    foreach (var node in proposed.Nodes) {
                        body.Children.Add(ServiceActionText(ServiceNodeName(node), 0xe9eef0));
                        var label = ServiceActionText(Loc.T("Čeká na výsledek…")); progressLabels.Add(node.Id, label); body.Children.Add(label);
                    }
                    dialog.PrimaryButtonText = ""; dialog.CloseButtonText = Loc.T("Zrušit čekání");
                    return approved;
                }, progress, operation.Token);
                if (!serviceActionsClosed) {
                    RenderServiceActionResult(body, row.Name, result);
                    if (!result.ApplySent && serviceActionCancellationReason.Length > 0) body.Children.Add(ServiceActionText(serviceActionCancellationReason));
                }
            } catch (Exception error) when (error is IOException or ArgumentException or OperationCanceledException) {
                if (!serviceActionsClosed) {
                    body.Children.Clear(); body.Children.Add(ServiceActionText(error.Message));
                    body.Children.Add(ServiceActionText(serviceActionApproved ? Loc.T("Výsledek není potvrzený. Správce služeb mohl část požadavků přijmout.") : Loc.T("Změna nebyla odeslána.")));
                }
            } finally {
                running = false; serviceActionConfirmation?.TrySetResult(null); serviceActionReady = null;
                if (!serviceActionsClosed) { dialog.PrimaryButtonText = ""; dialog.CloseButtonText = Loc.T("Zavřít"); RefreshServiceActions(); }
            }
        }
        dialog.Opened += (_, _) => work = Run();
        dialog.Closing += (_, args) => {
            if (running && !serviceActionsClosed) { args.Cancel = true; operation.Cancel(); serviceActionConfirmation?.TrySetResult(null); }
        };
        try { await dialog.ShowAsync(); if (work is not null) await work; }
        finally {
            serviceActionBusy = serviceActionApproved = false; serviceActionTarget = null; serviceActionOriginalConfig = null;
            serviceActionOperation = null; serviceActionDialog = null; serviceActionConfirmation = null; serviceActionReady = null;
            if (!serviceActionsClosed) RefreshServiceActions();
        }
    }

    private static bool ServicePlanMatchesRow(ServiceActionPlan plan, ServiceRow row)
    {
        var rootNode = plan.Nodes[0]; var config = row.Config;
        return StringComparer.OrdinalIgnoreCase.Equals(rootNode.Name, row.Name) && rootNode.OriginalState == row.State && rootNode.Title == row.Title
            && rootNode.Command == config.Command && StringComparer.OrdinalIgnoreCase.Equals(rootNode.Account, config.Account)
            && rootNode.StartType == config.StartType && rootNode.Dependencies.SequenceEqual(config.Dependencies, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<uint[]?> ConfirmServiceActionAsync(ServiceActionPlan plan, ContentDialog dialog, StackPanel body, CancellationToken cancellation)
    {
        var promise = new TaskCompletionSource<uint[]?>(TaskCreationOptions.RunContinuationsAsynchronously); serviceActionConfirmation = promise;
        body.Children.Clear();
        body.Children.Add(ServiceActionText(Loc.F("Plán zahrnuje {0}. Zkontrolujte všechny položky. Potvrzení má limit 60 sekund.", ServiceText.Count(plan.Nodes.Length))));
        if (plan.Action is ServiceAction.Start or ServiceAction.Restart)
            body.Children.Add(ServiceActionText(Loc.T("Správce služeb Windows může při spuštění nebo restartu automaticky spustit předpoklady uvedené v konfiguraci služby.")));
        foreach (var node in plan.Nodes) {
            var details = new StackPanel { Spacing = 4 };
            details.Children.Add(ServiceActionText(ServiceNodeName(node), 0xe9eef0));
            details.Children.Add(ServiceActionText(Loc.F("Stav: {0} → {1}", ServiceText.State(node.OriginalState), ServiceText.State(node.DesiredState))
                + (node.Flags.HasFlag(ServicePlanFlags.StopRequired) ? Loc.F(" · zastavení #{0}", node.StopOrder + 1) : "")
                + (node.Flags.HasFlag(ServicePlanFlags.StartRequired) ? Loc.F(" · spuštění #{0}", node.StartOrder + 1) : "")));
            details.Children.Add(ServiceActionText(Loc.F("Účet: {0}", node.Account.Length == 0 ? Loc.T("neuveden") : FlatServiceAction(node.Account))));
            details.Children.Add(ServiceActionText(Loc.F("Příkaz: {0}", node.Command.Length == 0 ? Loc.T("neuveden") : FlatServiceAction(node.Command))));
            details.Children.Add(ServiceActionText(Loc.F("Konfigurované předpoklady: {0}", node.Dependencies.Length == 0 ? Loc.T("žádné") : string.Join(", ", node.Dependencies.Select(FlatServiceAction)))));
            body.Children.Add(Card(details, new Thickness(10)));
        }
        var dependents = plan.Nodes.Where(node => node.Id != 0 && node.Flags.HasFlag(ServicePlanFlags.StopRequired)).ToArray();
        CheckBox? consent = null;
        if (dependents.Length > 0) {
            body.Children.Add(ServiceActionText(Loc.F("Současně se zastaví tyto závislé služby: {0}", string.Join(", ", dependents.Select(node => FlatServiceAction(node.Name))))));
            consent = new CheckBox { Content = ServiceActionText(Loc.T("Souhlasím se zastavením všech těchto závislých služeb"), 0xe9eef0),
                IsChecked = false, FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(consent, Loc.T("Výslovně schválit zastavení všech vypsaných závislých služeb")); body.Children.Add(consent);
        }
        bool Ready() => !cancellation.IsCancellationRequested && ServiceActionSelectionCurrent() && (consent is null || consent.IsChecked == true);
        serviceActionReady = Ready; dialog.PrimaryButtonText = Loc.T("Provést plán"); dialog.CloseButtonText = Loc.T("Zrušit"); dialog.IsPrimaryButtonEnabled = Ready();
        if (consent is not null) {
            consent.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = Ready();
            consent.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = Ready();
        }
        void Approve(ContentDialog sender, ContentDialogButtonClickEventArgs args) {
            args.Cancel = true;
            if (!Ready()) return;
            serviceActionApproved = true; dialog.IsPrimaryButtonEnabled = false;
            promise.TrySetResult(dependents.Select(node => node.Id).ToArray());
        }
        dialog.PrimaryButtonClick += Approve;
        using var registration = cancellation.Register(() => promise.TrySetResult(null));
        try { return await promise.Task; }
        finally { dialog.PrimaryButtonClick -= Approve; serviceActionConfirmation = null; serviceActionReady = null; }
    }

    private static void RenderServiceActionResult(StackPanel body, string name, ServiceActionsBatch result)
    {
        body.Children.Clear();
        body.Children.Add(ServiceActionText(result.Completed ? Loc.F("Potvrzená odpověď: {0}", ServiceActionsProtocol.StatusText(result.Status)) : Loc.F("Konečný výsledek není potvrzený: {0}", ServiceActionsProtocol.StatusText(result.Status)), 0xe9eef0));
        if (!result.ApplySent) body.Children.Add(ServiceActionText(Loc.T("Změna nebyla odeslána.")));
        if (result.Message.Length > 0) body.Children.Add(ServiceActionText(result.Message));
        if (result.Error != 0) body.Children.Add(ServiceActionText(ServiceWindowsError(Loc.T("Správce služeb"), result.Error)));
        if (result.Plan is null) { body.Children.Add(ServiceActionText(Loc.F("Vybraná služba: {0}", FlatServiceAction(name)))); return; }
        foreach (var node in result.Plan.Nodes) {
            body.Children.Add(ServiceActionText(ServiceNodeName(node), 0xe9eef0));
            var item = result.Items.FirstOrDefault(value => value.Id == node.Id);
            body.Children.Add(ServiceActionText(item is null ? result.ApplySent ? Loc.T("Výsledek není známý; požadavek se mohl provést.") : Loc.T("Neprovedeno.") : ServiceActionItemText(item)));
        }
    }
    private static string ServiceActionItemText(ServiceActionItem item)
    {
        string text = item.Outcome switch {
            ServiceActionOutcome.NotAttempted => item.Flags.HasFlag(ServiceResultFlags.MayHaveApplied) ? Loc.T("Požadavek se zpracovává; účinek zatím není potvrzený.") : Loc.T("Operace pro tuto službu nebyla zahájena."),
            ServiceActionOutcome.AlreadyDesired => Loc.T("Služba už měla požadovaný stav."),
            ServiceActionOutcome.Confirmed => Loc.T("Požadovaný stav byl potvrzen."), ServiceActionOutcome.Changed => Loc.T("Konfigurace nebo stav se změnily."),
            ServiceActionOutcome.Timeout => Loc.T("Vypršel čas čekání; výsledek není potvrzený."), ServiceActionOutcome.Unknown => Loc.T("Výsledek není známý; požadavek se mohl provést."),
            ServiceActionOutcome.Cancelled => Loc.T("Operace byla zrušena."), _ => Loc.T("Operace skončila chybou.")
        };
        if (item.Flags.HasFlag(ServiceResultFlags.StateKnown)) text += "\n" + Loc.F("Poslední pozorovaný stav: {0} · checkpoint {1} · doporučené čekání {2} ms.", ServiceText.State(item.LastState), item.Checkpoint, item.WaitHint);
        else text += "\n" + Loc.T("Stav není dostupný.");
        if (item.Flags.HasFlag(ServiceResultFlags.StopConfirmed)) text += "\n" + Loc.T("Zastavení bylo potvrzeno při pozorování.");
        if (item.Flags.HasFlag(ServiceResultFlags.StartConfirmed)) text += "\n" + Loc.T("Spuštění bylo potvrzeno při pozorování.");
        foreach (var (label, error) in new[] { (Loc.T("Pozorování"), item.ObservationError), (Loc.T("Zastavení"), item.StopError), (Loc.T("Spuštění"), item.StartError), (Loc.T("Ukončení služby"), item.Win32Exit) })
            if (error != 0) text += "\n" + ServiceWindowsError(label, error);
        if (item.ServiceExit != 0) text += "\n" + Loc.F("Vlastní návratový kód služby {0} (0x{1}).", item.ServiceExit, item.ServiceExit.ToString("X8"));
        if (item.Flags.HasFlag(ServiceResultFlags.MayHaveApplied) && item.Outcome is not (ServiceActionOutcome.Confirmed or ServiceActionOutcome.AlreadyDesired or ServiceActionOutcome.Unknown))
            text += "\n" + Loc.T("Správce služeb mohl část požadavků přijmout.");
        return text;
    }
    private ContentDialog ServiceActionDialog(string title, StackPanel content) => Appearance.Dialog(new ContentDialog {
        Title = title, XamlRoot = root.XamlRoot, CloseButtonText = Loc.T("Zavřít"), DefaultButton = ContentDialogButton.Close,
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(80, Math.Min(480, root.ActualHeight - 280)) }
    });
    private static TextBlock ServiceActionText(string text, uint color = 0x95a4ad) => new() { Text = text, FontFamily = UiTypography.Font, FontSize = 12,
        Foreground = Display.Brush(color), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static string FlatServiceAction(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    private static string ServiceNodeName(ServiceActionNode node) => FlatServiceAction(node.Title) + " (" + FlatServiceAction(node.Name) + ")"
        + (node.Flags.HasFlag(ServicePlanFlags.Root) ? Loc.T(" · vybraná služba") : Loc.T(" · závislá služba"));
    private static string ServiceActionTitle(ServiceAction action) => action switch { ServiceAction.Start => Loc.T("Spustit službu"), ServiceAction.Stop => Loc.T("Zastavit službu"), _ => Loc.T("Restartovat službu") };
    private static string ServiceWindowsError(string label, uint error) => label + ": Windows " + error + " (0x" + error.ToString("X8") + ").";
}
