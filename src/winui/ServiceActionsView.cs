using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Button serviceActionsButton = Button("Akce");
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
        AutomationProperties.SetName(serviceActionsButton, "Spustit, zastavit nebo restartovat vybranou službu");
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
        ToolTipService.SetToolTip(serviceActionsButton, serviceActionsClient.IsBusy ? "Akce služeb nebo ukončování jejího pracovníka ještě probíhá."
            : "Vyberte živou službu s dostupnou konfigurací. Windows ověří oprávnění bez automatického zvýšení práv.");
        if (!serviceActionBusy || serviceActionApproved) return;
        bool current = ServiceActionSelectionCurrent();
        if (!current) {
            serviceActionCancellationReason = "Výběr, stav nebo konfigurace služby se před schválením změnily. Vyberte znovu aktuální službu.";
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
        body.Children.Add(ServiceActionText("Načítám plán správce služeb Windows…"));
        var dialog = ServiceActionDialog(ServiceActionTitle(action), body); serviceActionDialog = dialog; dialog.CloseButtonText = "Zrušit";
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
                        serviceActionCancellationReason = "Plán už neodpovídá výběru, stavu nebo konfiguraci služby. Po obnovení seznamu jej vytvořte znovu."; return null;
                    }
                    var approved = await ConfirmServiceActionAsync(proposed, dialog, body, cancellation);
                    if (approved is null) return null;
                    body.Children.Clear();
                    body.Children.Add(ServiceActionText("Provádím schválený plán. Zrušení čekání nevrátí již přijaté požadavky správce služeb Windows."));
                    foreach (var node in proposed.Nodes) {
                        body.Children.Add(ServiceActionText(ServiceNodeName(node), 0xe9eef0));
                        var label = ServiceActionText("Čeká na výsledek…"); progressLabels.Add(node.Id, label); body.Children.Add(label);
                    }
                    dialog.PrimaryButtonText = ""; dialog.CloseButtonText = "Zrušit čekání";
                    return approved;
                }, progress, operation.Token);
                if (!serviceActionsClosed) {
                    RenderServiceActionResult(body, row.Name, result);
                    if (!result.ApplySent && serviceActionCancellationReason.Length > 0) body.Children.Add(ServiceActionText(serviceActionCancellationReason));
                }
            } catch (Exception error) when (error is IOException or ArgumentException or OperationCanceledException) {
                if (!serviceActionsClosed) {
                    body.Children.Clear(); body.Children.Add(ServiceActionText(error.Message));
                    body.Children.Add(ServiceActionText(serviceActionApproved ? "Výsledek není potvrzený. Správce služeb mohl část požadavků přijmout." : "Změna nebyla odeslána."));
                }
            } finally {
                running = false; serviceActionConfirmation?.TrySetResult(null); serviceActionReady = null;
                if (!serviceActionsClosed) { dialog.PrimaryButtonText = ""; dialog.CloseButtonText = "Zavřít"; RefreshServiceActions(); }
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
        body.Children.Add(ServiceActionText("Plán zahrnuje " + ServiceText.Count(plan.Nodes.Length) + ". Zkontrolujte všechny položky. Potvrzení má limit 60 sekund."));
        if (plan.Action is ServiceAction.Start or ServiceAction.Restart)
            body.Children.Add(ServiceActionText("Správce služeb Windows může při spuštění nebo restartu automaticky spustit předpoklady uvedené v konfiguraci služby."));
        foreach (var node in plan.Nodes) {
            var details = new StackPanel { Spacing = 4 };
            details.Children.Add(ServiceActionText(ServiceNodeName(node), 0xe9eef0));
            details.Children.Add(ServiceActionText("Stav: " + ServiceText.State(node.OriginalState) + " → " + ServiceText.State(node.DesiredState)
                + (node.Flags.HasFlag(ServicePlanFlags.StopRequired) ? " · zastavení #" + (node.StopOrder + 1) : "")
                + (node.Flags.HasFlag(ServicePlanFlags.StartRequired) ? " · spuštění #" + (node.StartOrder + 1) : "")));
            details.Children.Add(ServiceActionText("Účet: " + (node.Account.Length == 0 ? "neuveden" : FlatServiceAction(node.Account))));
            details.Children.Add(ServiceActionText("Příkaz: " + (node.Command.Length == 0 ? "neuveden" : FlatServiceAction(node.Command))));
            details.Children.Add(ServiceActionText("Konfigurované předpoklady: " + (node.Dependencies.Length == 0 ? "žádné" : string.Join(", ", node.Dependencies.Select(FlatServiceAction)))));
            body.Children.Add(Card(details, new Thickness(10)));
        }
        var dependents = plan.Nodes.Where(node => node.Id != 0 && node.Flags.HasFlag(ServicePlanFlags.StopRequired)).ToArray();
        CheckBox? consent = null;
        if (dependents.Length > 0) {
            body.Children.Add(ServiceActionText("Současně se zastaví tyto závislé služby: " + string.Join(", ", dependents.Select(node => FlatServiceAction(node.Name)))));
            consent = new CheckBox { Content = ServiceActionText("Souhlasím se zastavením všech těchto závislých služeb", 0xe9eef0),
                IsChecked = false, FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(consent, "Výslovně schválit zastavení všech vypsaných závislých služeb"); body.Children.Add(consent);
        }
        bool Ready() => !cancellation.IsCancellationRequested && ServiceActionSelectionCurrent() && (consent is null || consent.IsChecked == true);
        serviceActionReady = Ready; dialog.PrimaryButtonText = "Provést plán"; dialog.CloseButtonText = "Zrušit"; dialog.IsPrimaryButtonEnabled = Ready();
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
        body.Children.Add(ServiceActionText((result.Completed ? "Potvrzená odpověď: " : "Konečný výsledek není potvrzený: ") + ServiceActionsProtocol.StatusText(result.Status), 0xe9eef0));
        if (!result.ApplySent) body.Children.Add(ServiceActionText("Změna nebyla odeslána."));
        if (result.Message.Length > 0) body.Children.Add(ServiceActionText(result.Message));
        if (result.Error != 0) body.Children.Add(ServiceActionText(ServiceWindowsError("Správce služeb", result.Error)));
        if (result.Plan is null) { body.Children.Add(ServiceActionText("Vybraná služba: " + FlatServiceAction(name))); return; }
        foreach (var node in result.Plan.Nodes) {
            body.Children.Add(ServiceActionText(ServiceNodeName(node), 0xe9eef0));
            var item = result.Items.FirstOrDefault(value => value.Id == node.Id);
            body.Children.Add(ServiceActionText(item is null ? result.ApplySent ? "Výsledek není známý; požadavek se mohl provést." : "Neprovedeno." : ServiceActionItemText(item)));
        }
    }
    private static string ServiceActionItemText(ServiceActionItem item)
    {
        string text = item.Outcome switch {
            ServiceActionOutcome.NotAttempted => item.Flags.HasFlag(ServiceResultFlags.MayHaveApplied) ? "Požadavek se zpracovává; účinek zatím není potvrzený." : "Operace pro tuto službu nebyla zahájena.",
            ServiceActionOutcome.AlreadyDesired => "Služba už měla požadovaný stav.",
            ServiceActionOutcome.Confirmed => "Požadovaný stav byl potvrzen.", ServiceActionOutcome.Changed => "Konfigurace nebo stav se změnily.",
            ServiceActionOutcome.Timeout => "Vypršel čas čekání; výsledek není potvrzený.", ServiceActionOutcome.Unknown => "Výsledek není známý; požadavek se mohl provést.",
            ServiceActionOutcome.Cancelled => "Operace byla zrušena.", _ => "Operace skončila chybou."
        };
        if (item.Flags.HasFlag(ServiceResultFlags.StateKnown)) text += "\nPoslední pozorovaný stav: " + ServiceText.State(item.LastState)
            + " · checkpoint " + item.Checkpoint + " · doporučené čekání " + item.WaitHint + " ms.";
        else text += "\nStav není dostupný.";
        if (item.Flags.HasFlag(ServiceResultFlags.StopConfirmed)) text += "\nZastavení bylo potvrzeno při pozorování.";
        if (item.Flags.HasFlag(ServiceResultFlags.StartConfirmed)) text += "\nSpuštění bylo potvrzeno při pozorování.";
        foreach (var (label, error) in new[] { ("Pozorování", item.ObservationError), ("Zastavení", item.StopError), ("Spuštění", item.StartError), ("Ukončení služby", item.Win32Exit) })
            if (error != 0) text += "\n" + ServiceWindowsError(label, error);
        if (item.ServiceExit != 0) text += "\nVlastní návratový kód služby " + item.ServiceExit + " (0x" + item.ServiceExit.ToString("X8") + ").";
        if (item.Flags.HasFlag(ServiceResultFlags.MayHaveApplied) && item.Outcome is not (ServiceActionOutcome.Confirmed or ServiceActionOutcome.AlreadyDesired or ServiceActionOutcome.Unknown))
            text += "\nSprávce služeb mohl část požadavků přijmout.";
        return text;
    }
    private ContentDialog ServiceActionDialog(string title, StackPanel content) => Appearance.Dialog(new ContentDialog {
        Title = title, XamlRoot = root.XamlRoot, CloseButtonText = "Zavřít", DefaultButton = ContentDialogButton.Close,
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(80, Math.Min(480, root.ActualHeight - 280)) }
    });
    private static TextBlock ServiceActionText(string text, uint color = 0x95a4ad) => new() { Text = text, FontFamily = UiTypography.Font, FontSize = 12,
        Foreground = Display.Brush(color), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static string FlatServiceAction(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    private static string ServiceNodeName(ServiceActionNode node) => FlatServiceAction(node.Title) + " (" + FlatServiceAction(node.Name) + ")"
        + (node.Flags.HasFlag(ServicePlanFlags.Root) ? " · vybraná služba" : " · závislá služba");
    private static string ServiceActionTitle(ServiceAction action) => action switch { ServiceAction.Start => "Spustit službu", ServiceAction.Stop => "Zastavit službu", _ => "Restartovat službu" };
    private static string ServiceWindowsError(string label, uint error) => label + ": Windows " + error + " (0x" + error.ToString("X8") + ").";
}
