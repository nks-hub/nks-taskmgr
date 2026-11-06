using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using NksTaskMgr.Native;
using System.Runtime.InteropServices;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Button processDumpButton = Button(Loc.T("Dump"));
    private readonly ProcessDumpClient processDumpClient = new();
    private readonly MenuFlyoutItem captureDumpCommand = new() { Text = Loc.T("Vytvořit dump procesu…") };
    private DumpRecoveryWindow? dumpRecoveryWindow;
    private CancellationTokenSource? dumpOperation;
    private ContentDialog? dumpDialog;
    private TextBlock? dumpCleanup;
    private ProcessRow? dumpTargetRow;
    private ProcessDumpTarget? dumpTarget;
    private bool dumping, dumpClosed;

    private void ConfigureProcessDump()
    {
        processDumpButton.Height = 29;
        captureDumpCommand.IsEnabled = false;
        AutomationProperties.SetName(processDumpButton, Loc.T("Dump procesu a nedokončené dumpy"));
        AutomationProperties.SetAutomationId(processDumpButton, "process-dump");
        var menu = new MenuFlyout(); menu.Items.Add(captureDumpCommand);
        var recovery = new MenuFlyoutItem { Text = Loc.T("Nedokončené dumpy…") }; menu.Items.Add(recovery); processDumpButton.Flyout = menu;
        captureDumpCommand.Click += async (_, _) => await CreateProcessDumpAsync();
        recovery.Click += (_, _) => {
            if (dumpRecoveryWindow is null) {
                var window = new DumpRecoveryWindow(); dumpRecoveryWindow = window;
                window.Closed += (_, _) => { if (ReferenceEquals(dumpRecoveryWindow, window)) dumpRecoveryWindow = null; };
            }
            dumpRecoveryWindow.Activate();
        };
        Closed += (_, _) => { dumpClosed = true; dumpOperation?.Cancel(); dumpDialog?.Hide(); dumpRecoveryWindow?.Close(); processDumpClient.Dispose(); };
    }
    private bool CanDump(ProcessRow row) => model.IsCurrent(row) && !row.Stale && !row.Key.Boot.IsEmpty && row.Key.Pid is not (0 or 4)
        && row.Key.Kind is GenerationKind.CreationTime or GenerationKind.SequenceNumber && row.Key.Generation != 0 && row.Created100ns != 0;
    private bool DumpSelectionCurrent() => dumpTargetRow is { } row && dumpTarget is { } target && CanDump(row)
        && row.Key == target.Key && row.Created100ns == target.Created100ns && ReferenceEquals(SelectedProcess, row) && processList.SelectedItems.Count == 1;
    private void RefreshProcessDump()
    {
        SetEnabled(processDumpButton, !dumpClosed && !dumping);
        SetEnabled(captureDumpCommand, !dumping && !processDumpClient.IsBusy && processList.SelectedItems.Count == 1 && SelectedProcess is { } row && CanDump(row));
        SetToolTip(processDumpButton, processDumpClient.CleanupError ?? (processDumpClient.IsBusy ? Loc.T("Probíhá dump nebo úklid vlastního dočasného souboru.") : Loc.T("Dump x64 nebo 32bitového procesu po potvrzení obsahu a cílového souboru")));
        if (dumpCleanup is { } cleanup) cleanup.Text = processDumpClient.CleanupError ?? (processDumpClient.IsBusy ? Loc.T("Čekám na dokončení pracovníka a vypořádání vlastního dočasného souboru…") : "");
        if (dumping && !DumpSelectionCurrent()) { dumpOperation?.Cancel(); if (dumpDialog is { } dialog) dialog.IsPrimaryButtonEnabled = false; }
    }

    private async Task CreateProcessDumpAsync()
    {
        if (dumping || dumpClosed || processDumpClient.IsBusy || processList.SelectedItems.Count != 1 || SelectedProcess is not { } row || !CanDump(row)) return;
        dumping = true; dumpTargetRow = row; dumpTarget = new(row.Key, row.Created100ns);
        using var operation = new CancellationTokenSource(); dumpOperation = operation; RefreshProcessDump();
        try {
            var full = new CheckBox { Content = Loc.T("Zahrnout úplnou paměť procesu"), IsChecked = false, FontSize = 12 };
            AutomationProperties.SetName(full, Loc.T("Úplná paměť procesu, ve výchozím stavu vypnuto"));
            var content = new StackPanel { Spacing = 12, MinWidth = 280 };
            content.Children.Add(DumpText(FlatDumpName(row.Name) + " · PID " + row.Key.Pid, 0xe9eef0));
            content.Children.Add(DumpText(Loc.T("Dump ukládá informace o procesu, vláknech, modulech a části paměti. Může obsahovat hesla, osobní údaje i obsah otevřených dokumentů.")));
            content.Children.Add(full);
            content.Children.Add(DumpText(Loc.T("Úplný dump obsahuje veškerou dostupnou paměť a může zabrat mnoho GiB. Vytváření může krátce pozastavit odezvu procesu. Limit je 32 GiB a 60 sekund.")));
            content.Children.Add(DumpText(Loc.T("Ukládejte pouze do místního adresáře, kterému důvěřujete. Existující soubor se nepřepíše; odkazy a přesměrované adresáře se odmítnou.")));
            var confirm = DumpDialog(Loc.T("Vytvořit dump procesu"), content); confirm.PrimaryButtonText = Loc.T("Vybrat soubor…"); confirm.CloseButtonText = Loc.T("Zrušit");
            confirm.PrimaryButtonClick += (_, args) => { if (!DumpSelectionCurrent() || operation.IsCancellationRequested) args.Cancel = true; };
            dumpDialog = confirm;
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            dumpDialog = null;
            var picker = new FileSavePicker(AppWindow.Id) {
                SuggestedFileName = "process-" + row.Key.Pid + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                DefaultFileExtension = ".dmp", ShowOverwritePrompt = false
            };
            picker.FileTypeChoices.Add(Loc.T("Dump procesu"), new List<string> { ".dmp" });
            var file = await picker.PickSaveFileAsync();
            if (file is null || dumpClosed) return;
            if (!DumpSelectionCurrent() || operation.IsCancellationRequested) {
                await ShowDumpMessageAsync(Loc.T("Dump nebyl zahájen"), Loc.T("Výběr se změnil nebo proces skončil. Vyberte znovu živý proces.")); return;
            }
            await RunProcessDumpAsync(dumpTarget!, full.IsChecked == true ? ProcessDumpMode.Full : ProcessDumpMode.Mini, file.Path, operation);
        } catch (Exception error) when (error is IOException or ArgumentException or COMException or OperationCanceledException) {
            if (!dumpClosed) await ShowDumpMessageAsync(Loc.T("Dump nebyl dokončen"), error is OperationCanceledException ? Loc.T("Čekání bylo zrušeno. Výsledek není potvrzený.") : error.Message);
        } finally {
            dumping = false; dumpTargetRow = null; dumpTarget = null; dumpOperation = null; dumpDialog = null; dumpCleanup = null;
            if (!dumpClosed) RefreshProcessDump();
        }
    }

    private async Task RunProcessDumpAsync(ProcessDumpTarget target, ProcessDumpMode mode, string path, CancellationTokenSource operation)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(DumpText(mode == ProcessDumpMode.Full ? Loc.T("Vytvářím úplný dump. Soubor se zveřejní až po dokončení a ověření.") : Loc.T("Vytvářím běžný dump. Soubor se zveřejní až po dokončení a ověření.")));
        var resultText = DumpText(Loc.T("Limit 60 sekund · 32 GiB")); content.Children.Add(resultText);
        dumpCleanup = DumpText(""); content.Children.Add(dumpCleanup);
        var dialog = DumpDialog(Loc.F("Dump procesu · PID {0}", target.Key.Pid), content); dialog.CloseButtonText = Loc.T("Zrušit čekání");
        bool running = true; Task? work = null;
        async Task Run() {
            try {
                var result = await processDumpClient.CaptureAsync(target, mode, path, operation.Token);
                resultText.Text = DumpResultText(result);
            } catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or ArgumentException) {
                resultText.Text = (error is OperationCanceledException ? Loc.T("Čekání bylo zrušeno.") : error.Message)
                    + Loc.T("\nDump není potvrzený jako uložený. Finální soubor vzniklý těsně před zrušením zůstane zachovaný. Hotová nepublikovaná data zůstanou pro ruční obnovu; nedokončený vlastní soubor se po konci pracovníka uklidí.");
            } finally {
                running = false; if (!dumpClosed) { dialog.CloseButtonText = Loc.T("Zavřít"); RefreshProcessDump(); }
            }
        }
        dialog.Opened += (_, _) => work = Run();
        dialog.Closing += (_, args) => { if (running && !dumpClosed) { args.Cancel = true; operation.Cancel(); } };
        dumpDialog = dialog; await dialog.ShowAsync(); if (work is not null) await work;
        dumpDialog = null; dumpCleanup = null;
    }
    private async Task ShowDumpMessageAsync(string title, string message)
    {
        if (dumpClosed) return;
        var content = new StackPanel { Spacing = 10 }; content.Children.Add(DumpText(message));
        dumpDialog = DumpDialog(title, content); await dumpDialog.ShowAsync(); dumpDialog = null;
    }
    private ContentDialog DumpDialog(string title, StackPanel content) => Appearance.Dialog(new ContentDialog {
        Title = title, XamlRoot = root.XamlRoot, CloseButtonText = Loc.T("Zavřít"), DefaultButton = ContentDialogButton.Close,
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(80, Math.Min(460, root.ActualHeight - 280)) }
    });
    private static TextBlock DumpText(string text, uint color = 0x95a4ad) => new() {
        Text = text, FontFamily = UiTypography.Font, FontSize = 12, Foreground = Display.Brush(color), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
    };
    private static string FlatDumpName(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    private static string DumpResultText(ProcessDumpResult result)
    {
        if (result.State == ProcessDumpState.Captured && result.SavedPath is { } path) return Loc.F("Dump byl uložen.\n{0}", path)
            + "\n" + (result.Architecture == ProcessArchitecture.X86 ? "x86 (WOW64)" : "x64") + " · " + Display.Number(result.FileBytes / 1048576d, " MiB", 1)
            + Loc.F("\nPříznaky zachycení: 0x{0}", result.CaptureFlags.ToString("X8")) + "\nSHA-256: " + result.Sha256
            + (result.RecoveryWarning is null ? "" : "\n" + result.RecoveryWarning);
        string message = result.State switch {
            ProcessDumpState.Denied => Loc.T("Windows nebo pravidla ochrany cíle dump odmítly."), ProcessDumpState.Unsupported => Loc.T("Architektura nebo potřebná funkce není podporovaná."),
            ProcessDumpState.TargetChanged => Loc.T("Proces skončil nebo se změnila jeho identita."), ProcessDumpState.Unavailable => Loc.T("Cíl není dostupný."),
            ProcessDumpState.LimitExceeded => Loc.T("Dump překročil limit 32 GiB."), ProcessDumpState.Canceled => Loc.T("Vytváření bylo zrušeno nebo překročilo časový limit."),
            ProcessDumpState.InvalidRequest => Loc.T("Pracovník odmítl neplatný požadavek nebo nesouhlasící soubor."), _ => Loc.T("Dump se nepodařilo ověřit.")
        };
        return message + Loc.T(" Soubor nebyl zveřejněn.") + (result.Error == 0 ? "" : "\nWindows/HRESULT " + result.Error + " (0x" + result.Error.ToString("X8") + ").");
    }
}
