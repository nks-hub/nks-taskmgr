using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Button processActionsButton = Button(Loc.T("Akce"));
    private readonly ProcessActionsClient processActionsClient = new();
    private readonly ElevationBrokerClient elevationBrokerClient = new();
    private CancellationTokenSource? processActionOperation;
    private ProcessRow[] processActionTargets = [];
    private ContentDialog? processActionDialog;
    private Func<bool>? processActionInputReady;
    private bool processActionBusy, processActionsClosed, processActionDispatched;

    private void ConfigureProcessActions()
    {
        processActionsButton.Height = 29; processActionsButton.IsEnabled = false;
        AutomationProperties.SetName(processActionsButton, Loc.T("Akce s vybranými živými procesy"));
        AutomationProperties.SetAutomationId(processActionsButton, "process-actions");
        var menu = new MenuFlyout();
        foreach (var command in new[] { ProcessActionCommand.CloseWindows, ProcessActionCommand.Terminate, ProcessActionCommand.TerminateTree,
            ProcessActionCommand.Suspend, ProcessActionCommand.Resume, ProcessActionCommand.Priority, ProcessActionCommand.Affinity, ProcessActionCommand.CpuSets }) {
            var item = new MenuFlyoutItem { Text = ActionTitle(command) + "…" };
            item.Click += async (_, _) => await StartProcessActionAsync(command); menu.Items.Add(item);
        }
        processActionsButton.Flyout = menu;
        var administrator = new MenuFlyoutSubItem { Text = Loc.T("Se zvýšenými právy správce") };
        foreach (var command in new[] { ProcessActionCommand.CloseWindows, ProcessActionCommand.Terminate, ProcessActionCommand.TerminateTree,
            ProcessActionCommand.Suspend, ProcessActionCommand.Resume, ProcessActionCommand.Priority, ProcessActionCommand.Affinity, ProcessActionCommand.CpuSets }) {
            var item = new MenuFlyoutItem { Text = Loc.F("{0} jako správce…", ActionTitle(command)) };
            item.Click += async (_, _) => await StartProcessActionAsync(command, elevated: true); administrator.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(administrator);
        Closed += (_, _) => {
            processActionsClosed = true; processActionOperation?.Cancel(); processActionDialog?.Hide(); processActionsClient.Dispose(); elevationBrokerClient.Dispose();
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
        SetEnabled(processActionsButton, !processActionBusy && !processActionsClient.IsBusy && !elevationBrokerClient.IsBusy && rows.Length is > 0 and <= 64 && rows.All(Actionable));
        SetToolTip(processActionsButton, processActionsClient.IsBusy || elevationBrokerClient.IsBusy ? Loc.T("Čekám na ukončení vlastního pracovníka nebo zvýšeného požadavku. Neopakujte akci.") : rows.Length > 64 ? Loc.T("Vyberte nejvýše 64 procesů.")
            : Loc.T("Zavření oken, ukončení, pozastavení, priorita a afinita živých procesů stejného uživatele a relace"));
        if (!processActionBusy) return;
        bool current = ActionSelectionUnchanged(processActionTargets);
        if (!current && !processActionDispatched) processActionOperation?.Cancel();
        if (processActionInputReady is { } ready && processActionDialog is { } dialog)
            dialog.IsPrimaryButtonEnabled = current && ready();
    }

    private async Task StartProcessActionAsync(ProcessActionCommand command, bool elevated = false)
    {
        if (processActionBusy || processActionsClosed) return;
        var rows = ActionSelection();
        if (rows.Length is < 1 or > 64 || !rows.All(Actionable)) return;
        processActionBusy = true; processActionTargets = rows;
        using var operation = new CancellationTokenSource(); processActionOperation = operation; RefreshProcessActions();
        try {
            if (command == ProcessActionCommand.TerminateTree && rows.Length != 1) {
                await ActionMessageAsync(Loc.T("Ukončit strom"), rows, Loc.T("Vyberte jeden živý kořen. Potomky znovu zjistí a ověří pracovník.")); return;
            }
            if (command is ProcessActionCommand.Affinity or ProcessActionCommand.CpuSets && rows.Length != 1) {
                await ActionMessageAsync(ActionTitle(command), rows, Loc.T("Vyberte jeden proces. Každý proces má vlastní přiřazení procesorů.")); return;
            }
            ProcessActionsBatch? query = null;
            if (command is ProcessActionCommand.Priority or ProcessActionCommand.Affinity or ProcessActionCommand.CpuSets) {
                bool cpuSets = command == ProcessActionCommand.CpuSets;
                footer.Text = cpuSets ? Loc.T("Načítám výchozí CPU sets a dostupné logické procesory…") : Loc.T("Načítám současnou prioritu a afinitu vybraných procesů…");
                query = await ExecuteActionRequestAsync(ActionRequest(cpuSets ? ProcessActionCommand.QueryCpuSets : ProcessActionCommand.Query, rows), elevated, operation.Token);
                if (query.Frame != (cpuSets ? ProcessActionFrame.CpuSets : ProcessActionFrame.Results)
                    || query.Results.Any(row => row.Outcome != ProcessActionOutcome.Inspected) || cpuSets && query.CpuSets?.Valid != true) {
                    await ActionResultsAsync(Loc.T("Nastavení procesu není dostupné"), rows, query); return;
                }
            }
            if (!ActionSelectionUnchanged(rows) || operation.IsCancellationRequested) return;
            var request = await ConfirmProcessActionAsync(command, rows, query);
            if (request is null || processActionsClosed) return;
            if (!ActionSelectionUnchanged(rows) || operation.IsCancellationRequested) {
                await ActionMessageAsync(Loc.T("Výběr se změnil"), rows, Loc.T("Akce nebyla odeslána. Vyberte znovu živý proces.")); return;
            }
            await ExecuteProcessActionAsync(request, rows, operation, elevated);
        } catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or OperationCanceledException) {
            if (!processActionsClosed) await ActionMessageAsync(Loc.T("Dotaz na nastavení nebyl dokončen"), rows,
                error is OperationCanceledException ? Loc.T("Výběr už není aktuální nebo byl dotaz zrušen. Změna nebyla odeslána.") : Loc.F("{0} Změna nebyla odeslána.", error.Message));
        } finally {
            processActionOperation = null; processActionTargets = []; processActionDialog = null; processActionInputReady = null;
            processActionBusy = processActionDispatched = false; if (!processActionsClosed) RefreshProcessActions();
        }
    }

    private static ProcessActionsRequest ActionRequest(ProcessActionCommand command, ProcessRow[] rows, ulong argument = 0, uint group = 0)
        => new(command, rows.Select(row => new ProcessActionTarget(row.Key, row.Created100ns)).ToArray(), argument, group);

    private async Task<ProcessActionsRequest?> ConfirmProcessActionAsync(ProcessActionCommand command, ProcessRow[] rows, ProcessActionsBatch? query)
    {
        if (command == ProcessActionCommand.CpuSets) return await ConfirmCpuSetsAsync(rows, query!.CpuSets!);
        var content = new StackPanel { Spacing = 10, MinWidth = 280 };
        content.Children.Add(ActionText(ActionExplanation(command)));
        foreach (var row in rows) content.Children.Add(ActionText(ActionTargetName(row), 0xe9eef0));
        ulong argument = 0; uint group = 0; bool valid = true;
        ComboBox? priority = null;
        var processors = new List<(CheckBox Choice, ulong Mask)>();
        if (command == ProcessActionCommand.Priority) {
            priority = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            foreach (var option in new[] { new ActionPriority(0x40, Loc.T("Nízká")), new ActionPriority(0x4000, Loc.T("Nižší než normální")),
                new ActionPriority(0x20, Loc.T("Normální")), new ActionPriority(0x8000, Loc.T("Vyšší než normální")), new ActionPriority(0x80, Loc.T("Vysoká")) }) priority.Items.Add(option);
            foreach (var result in query!.Results) {
                var name = rows.First(row => row.Key == result.Target.Key);
                content.Children.Add(ActionText(Loc.F("{0}: současná priorita {1}", ActionTargetName(name), result.Flags.HasFlag(ProcessActionFlags.PriorityAvailable)
                    ? PriorityName(result.PriorityClass) : Loc.T("nedostupná") + WindowsError(result.PriorityError))));
            }
            uint current = query.Results.All(row => row.Flags.HasFlag(ProcessActionFlags.PriorityAvailable) && row.PriorityClass == query.Results[0].PriorityClass)
                ? query.Results[0].PriorityClass : 0;
            priority.SelectedItem = priority.Items.OfType<ActionPriority>().FirstOrDefault(option => option.Value == current);
            AutomationProperties.SetName(priority, Loc.T("Nová priorita procesu")); content.Children.Add(priority);
            content.Children.Add(ActionText(Loc.T("Vysoká priorita může omezit odezvu ostatních aplikací.")));
        } else if (command == ProcessActionCommand.Affinity) {
            var result = query!.Results[0];
            if (!result.Flags.HasFlag(ProcessActionFlags.AffinityAvailable)) {
                await ActionMessageAsync(Loc.T("Afinita není dostupná"), rows,
                    Loc.T("Toto nastavení vyžaduje jedinou ověřenou skupinu procesorů a dostupné masky procesu i systému. Výchozí CPU sets napříč skupinami mají samostatnou nabídku.")
                    + WindowsError(result.AffinityError)); return null;
            }
            group = result.Group;
            content.Children.Add(ActionText(Loc.F("Skupina {0} · současná maska 0x{1} · dostupná 0x{2}", group, result.ProcessMask.ToString("X"), result.SystemMask.ToString("X"))));
            var choices = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 8, ItemWidth = 80, ItemHeight = 34 };
            for (int cpu = 0; cpu < 64; ++cpu) {
                ulong mask = 1ul << cpu;
                if ((result.SystemMask & mask) == 0) continue;
                var choice = new CheckBox { Content = "CPU " + cpu, IsChecked = (result.ProcessMask & mask) != 0, FontSize = 12, MinWidth = 0 };
                processors.Add((choice, mask)); choices.Children.Add(choice);
            }
            content.Children.Add(choices); content.Children.Add(ActionText(Loc.T("Vyberte alespoň jeden procesor. CPU sets se tímto ovládáním nenastavují.")));
        }
        var dialog = ActionDialog(ActionTitle(command), content); dialog.PrimaryButtonText = Loc.T("Provést"); dialog.CloseButtonText = Loc.T("Zrušit");
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

    private Task<ProcessActionsBatch> ExecuteActionRequestAsync(ProcessActionsRequest request, bool elevated, CancellationToken cancellation)
        => elevated ? elevationBrokerClient.ExecuteAsync(request, cancellation) : processActionsClient.ExecuteAsync(request, cancellation);

    private async Task ExecuteProcessActionAsync(ProcessActionsRequest request, ProcessRow[] rows, CancellationTokenSource operation, bool elevated = false)
    {
        var content = new StackPanel { Spacing = 10 }; content.Children.Add(ActionText(Loc.T("Čekám na výsledek pracovníka. Zrušení čekání nevrátí již provedenou akci.")));
        if (elevated) content.Children.Add(ActionText(Loc.T("Výslovně žádáte zvýšení práv. Následuje UAC (nejvýše 90 s) a vlastní potvrzení správce (60 s); samotná akce má limit 5 s. Nepotvrzený požadavek se automaticky neopakuje.")));
        var dialog = ActionDialog(ActionTitle(request.Command), content); dialog.CloseButtonText = Loc.T("Zrušit čekání");
        bool running = true; Task? work = null;
        async Task Run() {
            try {
                processActionDispatched = request.Command is not (ProcessActionCommand.Query or ProcessActionCommand.QueryCpuSets);
                var result = await ExecuteActionRequestAsync(request, elevated, operation.Token);
                content.Children.Clear(); AddActionResults(content, rows, result);
            } catch (ProcessTreeActionsUnknownException error) {
                content.Children.Clear(); AddUnknownTreeResults(content, rows[0], error.Plan, error.Message);
            } catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or OperationCanceledException) {
                content.Children.Clear();
                string reason = error is OperationCanceledException ? Loc.T("Čekání bylo zrušeno nebo se změnil vybraný proces.") : error.Message;
                foreach (var row in rows) content.Children.Add(ActionText(ActionTargetName(row) + "\n" + Loc.F("Výsledek není známý. Akce se mohla provést. {0}", reason)));
            } finally {
                running = false; if (!processActionsClosed) { dialog.CloseButtonText = Loc.T("Zavřít"); footer.Text = Loc.T("Výsledky procesové akce jsou v dialogu."); }
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
        Title = title, XamlRoot = root.XamlRoot, CloseButtonText = Loc.T("Zavřít"), DefaultButton = ContentDialogButton.Close,
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
        0x40 => Loc.T("nízká"), 0x4000 => Loc.T("nižší než normální"), 0x20 => Loc.T("normální"), 0x8000 => Loc.T("vyšší než normální"), 0x80 => Loc.T("vysoká"),
        0x100 => Loc.T("reálný čas (nelze nastavit)"), _ => "0x" + value.ToString("X")
    };
    private static string ActionTitle(ProcessActionCommand command) => command switch {
        ProcessActionCommand.CloseWindows => Loc.T("Požádat o zavření oken"), ProcessActionCommand.Terminate => Loc.T("Ukončit proces"),
        ProcessActionCommand.TerminateTree => Loc.T("Ukončit strom"),
        ProcessActionCommand.Suspend => Loc.T("Pozastavit proces"), ProcessActionCommand.Resume => Loc.T("Obnovit běh procesu"),
        ProcessActionCommand.Priority => Loc.T("Změnit prioritu"), ProcessActionCommand.Affinity => Loc.T("Afinita procesorů"),
        ProcessActionCommand.CpuSets or ProcessActionCommand.QueryCpuSets => Loc.T("Výchozí CPU sets"),
        ProcessActionCommand.CloseHandle => Loc.T("Zavřít handle v procesu"),
        ProcessActionCommand.TaskManager => Loc.T("Správce úloh Windows"), _ => Loc.T("Nastavení procesu")
    };
    private static string ActionExplanation(ProcessActionCommand command) => command switch {
        ProcessActionCommand.CloseWindows => Loc.T("Pošle požadavek zavření viditelným oknům vybraných procesů. Aplikace může nabídnout uložení; skutečné zavření není zaručené."),
        ProcessActionCommand.Terminate => Loc.T("Ukončí vybrané procesy bez nabídky uložení. Neuložená data mohou být ztracena."),
        ProcessActionCommand.TerminateTree => Loc.T("Pracovník znovu zjistí potomky tohoto kořene a ukončí ověřené děti před rodičem. Neuložená data mohou být ztracena. Nejvýše 256 cílů, tři průchody a pět sekund. Průchod není atomický; nově vzniklé nebo neověřitelné potomky nemusí zahrnout. Zrušení čekání provedené zásahy nevrací."),
        ProcessActionCommand.Suspend => Loc.T("Požádá systém o pozastavení procesu. Jeho okna přestanou reagovat a prostředky zůstanou obsazené."),
        ProcessActionCommand.Resume => Loc.T("Požádá systém o obnovení procesu. Výsledek nezaručuje běh všech vláken pozastavených jinými nástroji."),
        ProcessActionCommand.Priority => Loc.T("Nastaví prioritu plánování času CPU u vybraných procesů. Priorita reálného času není v nabídce."),
        ProcessActionCommand.Affinity => Loc.T("Omezí proces na vybrané logické procesory v jediné skupině."),
        ProcessActionCommand.CpuSets => Loc.T("Nastaví výchozí výběr logických procesorů i napříč skupinami. Výslovné volby jednotlivých vláken a pevná afinita mají přednost. Potvrzení nastavení není důkaz, na kterém procesoru se vlákno skutečně spustilo."), _ => ""
    };
    private static string WindowsError(uint error) => error == 0 ? "" : " Windows " + error + " (0x" + error.ToString("X8") + ").";

    private static void AddActionResults(StackPanel content, ProcessRow[] rows, ProcessActionsBatch result)
    {
        string brokerStatus = ElevationBrokerClient.Status(result.Error);
        if (brokerStatus.Length > 0) content.Children.Add(ActionText(brokerStatus));
        if (result.Command == ProcessActionCommand.TerminateTree) { AddTreeActionResults(content, rows[0], result); return; }
        if (result.Command is ProcessActionCommand.QueryCpuSets or ProcessActionCommand.CpuSets) { AddCpuSetsResults(content, rows[0], result); return; }
        content.Children.Add(ActionText(Loc.F("Odpověď {0} · {1}", result.ReceivedAt.LocalDateTime.ToString("HH:mm:ss"), ActionTitle(result.Command))));
        foreach (var row in rows) {
            string text = ActionTargetName(row) + "\n";
            if (result.Frame != ProcessActionFrame.Results) text += (result.Accepted ? Loc.T("Výsledek není známý. Akce se mohla provést.") : Loc.T("Pracovník požadavek nepřijal; akce nebyla provedena.")) + WindowsError(result.Error);
            else text += ActionResultText(result.Results.First(value => value.Target.Key == row.Key), result.Command);
            content.Children.Add(ActionText(text, 0xe9eef0));
        }
    }

    internal static string ActionResultText(ProcessActionResult result, ProcessActionCommand command)
    {
        if (command == ProcessActionCommand.TaskManager)
            return ReplacementText.Result(result) + WindowsError(result.Error) + "\n" + Loc.F("Aktuální stav: {0}", CurrentReplacementText());
        string text = result.Outcome switch {
            ProcessActionOutcome.Inspected => Loc.T("Nastavení bylo načteno."), ProcessActionOutcome.Applied => command is ProcessActionCommand.Terminate or ProcessActionCommand.TerminateTree ? Loc.T("Konec tohoto procesu byl potvrzen.")
                : command == ProcessActionCommand.CloseHandle ? result.Flags.HasFlag(ProcessActionFlags.ObjectMatched)
                    ? Loc.T("Handle byl zavřen. Název nebo otisk zavřeného objektu odpovídá potvrzenému objektu.")
                    : Loc.T("Zavřen objekt stejného typu a přístupu; totožnost s potvrzeným objektem nelze prokázat.") : Loc.T("Změna byla ověřena zpětným čtením."),
            ProcessActionOutcome.CloseRequested => Loc.T("Požadavek zavření byl odeslán; zavření není potvrzené."), ProcessActionOutcome.TerminationRequested => Loc.T("Požadavek ukončení byl přijat; konec procesu zatím není potvrzený."),
            ProcessActionOutcome.NativeRequestAccepted => Loc.T("Systém přijal požadavek. Stav všech vláken není potvrzený."), ProcessActionOutcome.Refused => Loc.T("Akce byla odmítnuta."),
            ProcessActionOutcome.IdentityChanged => result.Flags.HasFlag(ProcessActionFlags.MayHaveApplied) ? Loc.T("Identita procesu se změnila; výsledek není ověřený.") : Loc.T("Identita procesu se změnila. Akce nebyla provedena."),
            ProcessActionOutcome.AlreadyExited => Loc.T("Proces už skončil."),
            ProcessActionOutcome.NotPerformed => Loc.T("Zásah nebyl proveden."),
            ProcessActionOutcome.Unsupported => Loc.T("Operace není pro tento cíl podporovaná."), ProcessActionOutcome.Unverified => Loc.T("Výsledek nebyl ověřen. Akce se mohla provést."), _ => Loc.T("Akce skončila chybou.")
        };
        if (result.Reason != ProcessActionReason.None) text += " " + ActionReasonText(result.Reason);
        text += WindowsError(result.Error);
        if (result.NtStatus != 0) text += " NTSTATUS 0x" + unchecked((uint)result.NtStatus).ToString("X8") + ".";
        if (result.Flags.HasFlag(ProcessActionFlags.MayHaveApplied) && result.Outcome != ProcessActionOutcome.Unverified) text += " " + Loc.T("Akce se mohla provést.");
        if (command == ProcessActionCommand.CloseWindows) text += " " + Loc.F("Odesláno oknům: {0}, chyby odeslání: {1}.", result.WindowsPosted, result.WindowsFailed);
        return text;
    }
    private static string ActionReasonText(ProcessActionReason reason) => reason switch {
        ProcessActionReason.InvalidTarget => Loc.T("Neplatný cíl."), ProcessActionReason.OwnOrParent => Loc.T("Pracovník ani řídicí aplikace nejsou povoleným cílem."),
        ProcessActionReason.SystemTarget => Loc.T("Systémový nebo služební proces."), ProcessActionReason.Critical => Loc.T("Kritický proces systému."),
        ProcessActionReason.Protected => Loc.T("Chráněný proces."), ProcessActionReason.OwnerMismatch => Loc.T("Cíl patří jinému uživateli nebo relaci."),
        ProcessActionReason.AccessDenied => Loc.T("Windows odmítl přístup."), ProcessActionReason.IdentityUnverifiable => Loc.T("Identitu nelze ověřit."),
        ProcessActionReason.RawTimeMismatch => Loc.T("Čas vytvoření neodpovídá výběru."), ProcessActionReason.NoWindows => Loc.T("Proces nemá vhodné viditelné okno."),
        ProcessActionReason.WindowMismatch => Loc.T("Okno už nepatří tomuto procesu."), ProcessActionReason.InvalidArgument => Loc.T("Nepovolená hodnota nastavení."),
        ProcessActionReason.MultipleGroups => Loc.T("Více skupin procesorů není podporováno."), ProcessActionReason.PartialClose => Loc.T("Některá okna požadavek nepřijala."),
        ProcessActionReason.ReadbackMismatch => Loc.T("Zpětné čtení nepotvrdilo požadovanou hodnotu."), ProcessActionReason.ProtectionUnverifiable => Loc.T("Ochranu cíle nelze ověřit."),
        ProcessActionReason.ParentUnavailable => Loc.T("Identitu řídicí aplikace nelze ověřit."), ProcessActionReason.AlreadySame => Loc.T("Hodnota už byla nastavená."),
        ProcessActionReason.WindowLimit => Loc.T("Byl překročen omezený počet oken."), ProcessActionReason.NativeUnavailable => Loc.T("Potřebná systémová funkce není dostupná."),
        ProcessActionReason.TreeBoundary => Loc.T("Vazbu na potvrzený strom nelze ověřit."), ProcessActionReason.TreeBudget => Loc.T("Pro tento cíl nezbyl čas v omezeném průchodu."),
        ProcessActionReason.TreeCensus => Loc.T("Nový inventář stromu není dostupný."), ProcessActionReason.TreeUncertain => Loc.T("Zásah nebyl bezpečně ověřitelný."),
        ProcessActionReason.HandleMissing => Loc.T("Handle s touto hodnotou už v procesu není."),
        ProcessActionReason.HandleChanged => Loc.T("Pod hodnotou handle je jiný objekt, typ nebo přístup než při potvrzení."),
        ProcessActionReason.HandleUnverifiable => Loc.T("Objekt handle nelze ověřit."),
        ProcessActionReason.HandleProtected => Loc.T("Handle je chráněný proti zavření."), _ => ""
    };
}
