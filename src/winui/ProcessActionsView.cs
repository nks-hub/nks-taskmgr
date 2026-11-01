using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Button processActionsButton = Button("Akce");
    private readonly ProcessActionsClient processActionsClient = new();
    private CancellationTokenSource? processActionOperation;
    private ProcessRow[] processActionTargets = [];
    private ContentDialog? processActionDialog;
    private Func<bool>? processActionInputReady;
    private bool processActionBusy, processActionsClosed, processActionDispatched;

    private void ConfigureProcessActions()
    {
        processActionsButton.Height = 29; processActionsButton.IsEnabled = false;
        AutomationProperties.SetName(processActionsButton, "Akce s vybranými živými procesy");
        AutomationProperties.SetAutomationId(processActionsButton, "process-actions");
        var menu = new MenuFlyout();
        foreach (var command in new[] { ProcessActionCommand.CloseWindows, ProcessActionCommand.Terminate, ProcessActionCommand.TerminateTree,
            ProcessActionCommand.Suspend, ProcessActionCommand.Resume, ProcessActionCommand.Priority, ProcessActionCommand.Affinity }) {
            var item = new MenuFlyoutItem { Text = ActionTitle(command) + "…" };
            item.Click += async (_, _) => await StartProcessActionAsync(command); menu.Items.Add(item);
        }
        processActionsButton.Flyout = menu;
        Closed += (_, _) => {
            processActionsClosed = true; processActionOperation?.Cancel(); processActionDialog?.Hide(); processActionsClient.Dispose();
        };
    }

    private ProcessRow[] ActionSelection() => processList.SelectedItems.OfType<ProcessSlot>().Select(slot => slot.Row).ToArray();
    private bool Actionable(ProcessRow row) => model.IsCurrent(row) && !row.Stale && !row.Key.Boot.IsEmpty
        && row.Key.Kind is GenerationKind.CreationTime or GenerationKind.SequenceNumber
        && row.Key.Generation != 0 && row.Created100ns != 0 && row.Key.Pid is not (0 or 4) && row.Key.Pid != Environment.ProcessId;
    private bool ActionSelectionUnchanged(ProcessRow[] rows)
    {
        var selected = ActionSelection();
        return rows.Length == selected.Length && rows.All(row => Actionable(row) && selected.Contains(row));
    }

    private void RefreshProcessActions()
    {
        var rows = ActionSelection();
        processActionsButton.IsEnabled = !processActionBusy && !processActionsClient.IsBusy && rows.Length is > 0 and <= 64 && rows.All(Actionable);
        ToolTipService.SetToolTip(processActionsButton, processActionsClient.IsBusy ? "Čekám na ukončení vlastního pracovníka před další akcí." : rows.Length > 64 ? "Vyberte nejvýše 64 procesů."
            : "Zavření oken, ukončení, pozastavení, priorita a afinita živých procesů stejného uživatele a relace");
        if (!processActionBusy) return;
        bool current = ActionSelectionUnchanged(processActionTargets);
        if (!current && !processActionDispatched) processActionOperation?.Cancel();
        if (processActionInputReady is { } ready && processActionDialog is { } dialog)
            dialog.IsPrimaryButtonEnabled = current && ready();
    }

    private async Task StartProcessActionAsync(ProcessActionCommand command)
    {
        if (processActionBusy || processActionsClosed) return;
        var rows = ActionSelection();
        if (rows.Length is < 1 or > 64 || !rows.All(Actionable)) return;
        processActionBusy = true; processActionTargets = rows;
        using var operation = new CancellationTokenSource(); processActionOperation = operation; RefreshProcessActions();
        try {
            if (command == ProcessActionCommand.TerminateTree && rows.Length != 1) {
                await ActionMessageAsync("Ukončit strom", rows, "Vyberte jeden živý kořen. Potomky znovu zjistí a ověří pracovník."); return;
            }
            if (command == ProcessActionCommand.Affinity && rows.Length != 1) {
                await ActionMessageAsync("Afinita procesorů", rows, "Vyberte jeden proces. Afinita se nastavuje v jediné ověřené skupině procesorů."); return;
            }
            ProcessActionsBatch? query = null;
            if (command is ProcessActionCommand.Priority or ProcessActionCommand.Affinity) {
                footer.Text = "Načítám současnou prioritu a afinitu vybraných procesů…";
                query = await processActionsClient.ExecuteAsync(ActionRequest(ProcessActionCommand.Query, rows), operation.Token);
                if (query.Frame != ProcessActionFrame.Results || query.Results.Any(row => row.Outcome != ProcessActionOutcome.Inspected)) {
                    await ActionResultsAsync("Nastavení procesu není dostupné", rows, query); return;
                }
            }
            if (!ActionSelectionUnchanged(rows) || operation.IsCancellationRequested) return;
            var request = await ConfirmProcessActionAsync(command, rows, query);
            if (request is null || processActionsClosed) return;
            if (!ActionSelectionUnchanged(rows) || operation.IsCancellationRequested) {
                await ActionMessageAsync("Výběr se změnil", rows, "Akce nebyla odeslána. Vyberte znovu živý proces."); return;
            }
            await ExecuteProcessActionAsync(request, rows, operation);
        } catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or OperationCanceledException) {
            if (!processActionsClosed) await ActionMessageAsync("Dotaz na nastavení nebyl dokončen", rows,
                error is OperationCanceledException ? "Výběr už není aktuální nebo byl dotaz zrušen. Změna nebyla odeslána." : error.Message + " Změna nebyla odeslána.");
        } finally {
            processActionOperation = null; processActionTargets = []; processActionDialog = null; processActionInputReady = null;
            processActionBusy = processActionDispatched = false; if (!processActionsClosed) RefreshProcessActions();
        }
    }

    private static ProcessActionsRequest ActionRequest(ProcessActionCommand command, ProcessRow[] rows, ulong argument = 0, uint group = 0)
        => new(command, rows.Select(row => new ProcessActionTarget(row.Key, row.Created100ns)).ToArray(), argument, group);

    private async Task<ProcessActionsRequest?> ConfirmProcessActionAsync(ProcessActionCommand command, ProcessRow[] rows, ProcessActionsBatch? query)
    {
        var content = new StackPanel { Spacing = 10, MinWidth = 280 };
        content.Children.Add(ActionText(ActionExplanation(command)));
        foreach (var row in rows) content.Children.Add(ActionText(ActionTargetName(row), 0xe9eef0));
        ulong argument = 0; uint group = 0; bool valid = true;
        ComboBox? priority = null;
        var processors = new List<(CheckBox Choice, ulong Mask)>();
        if (command == ProcessActionCommand.Priority) {
            priority = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            foreach (var option in new[] { new ActionPriority(0x40, "Nízká"), new ActionPriority(0x4000, "Nižší než normální"),
                new ActionPriority(0x20, "Normální"), new ActionPriority(0x8000, "Vyšší než normální"), new ActionPriority(0x80, "Vysoká") }) priority.Items.Add(option);
            foreach (var result in query!.Results) {
                var name = rows.First(row => row.Key == result.Target.Key);
                content.Children.Add(ActionText(ActionTargetName(name) + ": současná priorita " + (result.Flags.HasFlag(ProcessActionFlags.PriorityAvailable)
                    ? PriorityName(result.PriorityClass) : "nedostupná" + WindowsError(result.PriorityError))));
            }
            uint current = query.Results.All(row => row.Flags.HasFlag(ProcessActionFlags.PriorityAvailable) && row.PriorityClass == query.Results[0].PriorityClass)
                ? query.Results[0].PriorityClass : 0;
            priority.SelectedItem = priority.Items.OfType<ActionPriority>().FirstOrDefault(option => option.Value == current);
            AutomationProperties.SetName(priority, "Nová priorita procesu"); content.Children.Add(priority);
            content.Children.Add(ActionText("Vysoká priorita může omezit odezvu ostatních aplikací."));
        } else if (command == ProcessActionCommand.Affinity) {
            var result = query!.Results[0];
            if (!result.Flags.HasFlag(ProcessActionFlags.AffinityAvailable)) {
                await ActionMessageAsync("Afinita není dostupná", rows,
                    "Nastavení vyžaduje jedinou ověřenou skupinu procesorů a dostupné masky procesu i systému. CPU sets ani více skupin nejsou podporované."
                    + WindowsError(result.AffinityError)); return null;
            }
            group = result.Group;
            content.Children.Add(ActionText("Skupina " + group + " · současná maska 0x" + result.ProcessMask.ToString("X") + " · dostupná 0x" + result.SystemMask.ToString("X")));
            var choices = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 8, ItemWidth = 80, ItemHeight = 34 };
            for (int cpu = 0; cpu < 64; ++cpu) {
                ulong mask = 1ul << cpu;
                if ((result.SystemMask & mask) == 0) continue;
                var choice = new CheckBox { Content = "CPU " + cpu, IsChecked = (result.ProcessMask & mask) != 0, FontSize = 12, MinWidth = 0 };
                processors.Add((choice, mask)); choices.Children.Add(choice);
            }
            content.Children.Add(choices); content.Children.Add(ActionText("Vyberte alespoň jeden procesor. CPU sets se tímto ovládáním nenastavují."));
        }
        var dialog = ActionDialog(ActionTitle(command), content); dialog.PrimaryButtonText = "Provést"; dialog.CloseButtonText = "Zrušit";
        dialog.DefaultButton = ContentDialogButton.Close;
        bool Ready() {
            if (priority is not null) { valid = priority.SelectedItem is ActionPriority; argument = (priority.SelectedItem as ActionPriority)?.Value ?? 0; }
            if (processors.Count > 0) { argument = processors.Aggregate(0ul, (mask, item) => item.Choice.IsChecked == true ? mask | item.Mask : mask); valid = argument != 0; }
            return valid;
        }
        void Refresh() => dialog.IsPrimaryButtonEnabled = !processActionsClosed && ActionSelectionUnchanged(rows) && Ready();
        if (priority is not null) priority.SelectionChanged += (_, _) => Refresh();
        foreach (var item in processors) { item.Choice.Checked += (_, _) => Refresh(); item.Choice.Unchecked += (_, _) => Refresh(); }
        dialog.PrimaryButtonClick += (_, args) => { if (!ActionSelectionUnchanged(rows) || !Ready()) args.Cancel = true; };
        processActionInputReady = Ready; processActionDialog = dialog; Refresh();
        var choiceResult = await dialog.ShowAsync(); processActionDialog = null; processActionInputReady = null;
        return choiceResult == ContentDialogResult.Primary ? ActionRequest(command, rows, argument, group) : null;
    }

    private async Task ExecuteProcessActionAsync(ProcessActionsRequest request, ProcessRow[] rows, CancellationTokenSource operation)
    {
        var content = new StackPanel { Spacing = 10 }; content.Children.Add(ActionText("Čekám na výsledek pracovníka. Zrušení čekání nevrátí již provedenou akci."));
        var dialog = ActionDialog(ActionTitle(request.Command), content); dialog.CloseButtonText = "Zrušit čekání";
        bool running = true; Task? work = null;
        async Task Run() {
            try {
                processActionDispatched = request.Command != ProcessActionCommand.Query;
                var result = await processActionsClient.ExecuteAsync(request, operation.Token);
                content.Children.Clear(); AddActionResults(content, rows, result);
            } catch (ProcessTreeActionsUnknownException error) {
                content.Children.Clear(); AddUnknownTreeResults(content, rows[0], error.Plan, error.Message);
            } catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or OperationCanceledException) {
                content.Children.Clear();
                string reason = error is OperationCanceledException ? "Čekání bylo zrušeno nebo se změnil vybraný proces." : error.Message;
                foreach (var row in rows) content.Children.Add(ActionText(ActionTargetName(row) + "\nVýsledek není známý. Akce se mohla provést. " + reason));
            } finally {
                running = false; if (!processActionsClosed) { dialog.CloseButtonText = "Zavřít"; footer.Text = "Výsledky procesové akce jsou v dialogu."; }
            }
        }
        dialog.Opened += (_, _) => work = Run();
        dialog.Closing += (_, args) => { if (running && !processActionsClosed) { args.Cancel = true; operation.Cancel(); } };
        processActionDialog = dialog; await dialog.ShowAsync();
        if (work is not null) await work;
        processActionDialog = null;
    }

    private async Task ActionMessageAsync(string title, ProcessRow[] rows, string message)
    {
        if (processActionsClosed) return;
        var content = new StackPanel { Spacing = 10 };
        foreach (var row in rows) content.Children.Add(ActionText(ActionTargetName(row) + "\n" + message));
        processActionDialog = ActionDialog(title, content); await processActionDialog.ShowAsync(); processActionDialog = null;
    }

    private async Task ActionResultsAsync(string title, ProcessRow[] rows, ProcessActionsBatch result)
    {
        if (processActionsClosed) return;
        var content = new StackPanel { Spacing = 10 }; AddActionResults(content, rows, result);
        processActionDialog = ActionDialog(title, content); await processActionDialog.ShowAsync(); processActionDialog = null;
    }

    private ContentDialog ActionDialog(string title, StackPanel content) => Appearance.Dialog(new ContentDialog {
        Title = title, XamlRoot = root.XamlRoot, CloseButtonText = "Zavřít", DefaultButton = ContentDialogButton.Close,
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(80, Math.Min(460, root.ActualHeight - 280)) }
    });
    private static TextBlock ActionText(string text, uint color = 0x95a4ad) => new() {
        Text = text, FontFamily = UiTypography.Font, FontSize = 12, Foreground = Display.Brush(color),
        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
    };
    private static string ActionTargetName(ProcessRow row) => row.Name.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ') + " · PID " + row.Key.Pid;
    private sealed record ActionPriority(uint Value, string Name) { public override string ToString() => Name; }
    private static string PriorityName(uint value) => value switch {
        0x40 => "nízká", 0x4000 => "nižší než normální", 0x20 => "normální", 0x8000 => "vyšší než normální", 0x80 => "vysoká",
        0x100 => "reálný čas (nelze nastavit)", _ => "0x" + value.ToString("X")
    };
    private static string ActionTitle(ProcessActionCommand command) => command switch {
        ProcessActionCommand.CloseWindows => "Požádat o zavření oken", ProcessActionCommand.Terminate => "Ukončit proces",
        ProcessActionCommand.TerminateTree => "Ukončit strom",
        ProcessActionCommand.Suspend => "Pozastavit proces", ProcessActionCommand.Resume => "Obnovit běh procesu",
        ProcessActionCommand.Priority => "Změnit prioritu", ProcessActionCommand.Affinity => "Afinita procesorů", _ => "Nastavení procesu"
    };
    private static string ActionExplanation(ProcessActionCommand command) => command switch {
        ProcessActionCommand.CloseWindows => "Pošle požadavek zavření viditelným oknům vybraných procesů. Aplikace může nabídnout uložení; skutečné zavření není zaručené.",
        ProcessActionCommand.Terminate => "Ukončí vybrané procesy bez nabídky uložení. Neuložená data mohou být ztracena.",
        ProcessActionCommand.TerminateTree => "Pracovník znovu zjistí potomky tohoto kořene a ukončí ověřené děti před rodičem. Neuložená data mohou být ztracena. Nejvýše 256 cílů, tři průchody a pět sekund. Průchod není atomický; nově vzniklé nebo neověřitelné potomky nemusí zahrnout. Zrušení čekání provedené zásahy nevrací.",
        ProcessActionCommand.Suspend => "Požádá systém o pozastavení procesu. Jeho okna přestanou reagovat a prostředky zůstanou obsazené.",
        ProcessActionCommand.Resume => "Požádá systém o obnovení procesu. Výsledek nezaručuje běh všech vláken pozastavených jinými nástroji.",
        ProcessActionCommand.Priority => "Nastaví prioritu plánování času CPU u vybraných procesů. Priorita reálného času není v nabídce.",
        ProcessActionCommand.Affinity => "Omezí proces na vybrané logické procesory v jediné skupině.", _ => ""
    };
    private static string WindowsError(uint error) => error == 0 ? "" : " Windows " + error + " (0x" + error.ToString("X8") + ").";

    private static void AddActionResults(StackPanel content, ProcessRow[] rows, ProcessActionsBatch result)
    {
        if (result.Command == ProcessActionCommand.TerminateTree) { AddTreeActionResults(content, rows[0], result); return; }
        content.Children.Add(ActionText("Odpověď " + result.ReceivedAt.LocalDateTime.ToString("HH:mm:ss") + " · " + ActionTitle(result.Command)));
        foreach (var row in rows) {
            string text = ActionTargetName(row) + "\n";
            if (result.Frame != ProcessActionFrame.Results) text += (result.Accepted ? "Výsledek není známý. Akce se mohla provést." : "Pracovník požadavek nepřijal; akce nebyla provedena.") + WindowsError(result.Error);
            else text += ActionResultText(result.Results.First(value => value.Target.Key == row.Key), result.Command);
            content.Children.Add(ActionText(text, 0xe9eef0));
        }
    }

    private static string ActionResultText(ProcessActionResult result, ProcessActionCommand command)
    {
        string text = result.Outcome switch {
            ProcessActionOutcome.Inspected => "Nastavení bylo načteno.", ProcessActionOutcome.Applied => command is ProcessActionCommand.Terminate or ProcessActionCommand.TerminateTree ? "Konec tohoto procesu byl potvrzen." : "Změna byla ověřena zpětným čtením.",
            ProcessActionOutcome.CloseRequested => "Požadavek zavření byl odeslán; zavření není potvrzené.", ProcessActionOutcome.TerminationRequested => "Požadavek ukončení byl přijat; konec procesu zatím není potvrzený.",
            ProcessActionOutcome.NativeRequestAccepted => "Systém přijal požadavek. Stav všech vláken není potvrzený.", ProcessActionOutcome.Refused => "Akce byla odmítnuta.",
            ProcessActionOutcome.IdentityChanged => result.Flags.HasFlag(ProcessActionFlags.MayHaveApplied) ? "Identita procesu se změnila; výsledek není ověřený." : "Identita procesu se změnila. Akce nebyla provedena.",
            ProcessActionOutcome.AlreadyExited => "Proces už skončil.",
            ProcessActionOutcome.NotPerformed => "Zásah nebyl proveden.",
            ProcessActionOutcome.Unsupported => "Operace není pro tento cíl podporovaná.", ProcessActionOutcome.Unverified => "Výsledek nebyl ověřen. Akce se mohla provést.", _ => "Akce skončila chybou."
        };
        if (result.Reason != ProcessActionReason.None) text += " " + ActionReasonText(result.Reason);
        text += WindowsError(result.Error);
        if (result.NtStatus != 0) text += " NTSTATUS 0x" + unchecked((uint)result.NtStatus).ToString("X8") + ".";
        if (result.Flags.HasFlag(ProcessActionFlags.MayHaveApplied) && result.Outcome != ProcessActionOutcome.Unverified) text += " Akce se mohla provést.";
        if (command == ProcessActionCommand.CloseWindows) text += " Odesláno oknům: " + result.WindowsPosted + ", chyby odeslání: " + result.WindowsFailed + ".";
        return text;
    }
    private static string ActionReasonText(ProcessActionReason reason) => reason switch {
        ProcessActionReason.InvalidTarget => "Neplatný cíl.", ProcessActionReason.OwnOrParent => "Pracovník ani řídicí aplikace nejsou povoleným cílem.",
        ProcessActionReason.SystemTarget => "Systémový nebo služební proces.", ProcessActionReason.Critical => "Kritický proces systému.",
        ProcessActionReason.Protected => "Chráněný proces.", ProcessActionReason.OwnerMismatch => "Cíl patří jinému uživateli nebo relaci.",
        ProcessActionReason.AccessDenied => "Windows odmítl přístup.", ProcessActionReason.IdentityUnverifiable => "Identitu nelze ověřit.",
        ProcessActionReason.RawTimeMismatch => "Čas vytvoření neodpovídá výběru.", ProcessActionReason.NoWindows => "Proces nemá vhodné viditelné okno.",
        ProcessActionReason.WindowMismatch => "Okno už nepatří tomuto procesu.", ProcessActionReason.InvalidArgument => "Nepovolená hodnota nastavení.",
        ProcessActionReason.MultipleGroups => "Více skupin procesorů není podporováno.", ProcessActionReason.PartialClose => "Některá okna požadavek nepřijala.",
        ProcessActionReason.ReadbackMismatch => "Zpětné čtení nepotvrdilo požadovanou hodnotu.", ProcessActionReason.ProtectionUnverifiable => "Ochranu cíle nelze ověřit.",
        ProcessActionReason.ParentUnavailable => "Identitu řídicí aplikace nelze ověřit.", ProcessActionReason.AlreadySame => "Hodnota už byla nastavená.",
        ProcessActionReason.WindowLimit => "Byl překročen omezený počet oken.", ProcessActionReason.NativeUnavailable => "Potřebná systémová funkce není dostupná.",
        ProcessActionReason.TreeBoundary => "Vazbu na potvrzený strom nelze ověřit.", ProcessActionReason.TreeBudget => "Pro tento cíl nezbyl čas v omezeném průchodu.",
        ProcessActionReason.TreeCensus => "Nový inventář stromu není dostupný.", ProcessActionReason.TreeUncertain => "Zásah nebyl bezpečně ověřitelný.", _ => ""
    };
}
