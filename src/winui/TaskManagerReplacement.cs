using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

/// <summary>Czech texts for the Windows Task Manager replacement; pure so the mapping can be checked without a window.</summary>
internal static class ReplacementText
{
    internal static string Title(ReplacementState state) => state.Error != 0 ? "Stav se nepodařilo přečíst" : state.Status switch {
        ReplacementStatus.NotRegistered => "Nenastaveno",
        ReplacementStatus.ThisInstall => "Nastaveno na tuto instalaci",
        ReplacementStatus.OtherNks => "Nastaveno na jinou instalaci NKS (stará cesta)",
        _ => "Nastaveno na jiný program"
    };

    internal static string Detail(ReplacementState state)
    {
        if (state.Error != 0) return "Windows " + state.Error + " (0x" + state.Error.ToString("X8") + ").";
        string command = Printable(state.Debugger);
        string restore = state.HasBackup
            ? state.BackupRestorable ? "Vypnutím se obnoví původní program."
                : "Původní program chybí nebo ho mohou měnit i běžní uživatelé, proto se nebude obnovovat; vypnutím se registrace jen odstraní."
            : "Vypnutím se registrace odstraní a Správce úloh Windows se spouští jako dříve.";
        return state.Status switch {
            ReplacementStatus.NotRegistered => "Správce úloh Windows se spouští jako dříve.",
            ReplacementStatus.ThisInstall => "Příkaz: " + command + "\n" + restore,
            ReplacementStatus.OtherNks => "Příkaz: " + command + "\nZapnutím se cesta opraví na tuto instalaci. " + restore,
            _ => "Příkaz: " + command + "\nZapnutím se tento příkaz uloží a přepíše; vypnutím se obnoví. Cizí registraci NKS sama nevypíná."
        };
    }

    /// <summary>Result of an elevated change as returned by the broker; the current state is appended by the caller.</summary>
    internal static string Result(ProcessActionResult result)
    {
        string text = result.Outcome switch {
            ProcessActionOutcome.Applied => "Registrace Správce úloh Windows byla změněna.",
            ProcessActionOutcome.Inspected => "Nic se nezměnilo, registrace už byla v požadovaném stavu.",
            ProcessActionOutcome.Refused => "Správce úloh je nastaven na jiný program než NKS. Registrace zůstala beze změny.",
            _ => "Změna registrace skončila chybou."
        };
        if (result.Outcome == ProcessActionOutcome.Applied && result.Reason == ProcessActionReason.ProtectionUnverifiable)
            text += " Původní program chybí nebo ho mohou měnit i běžní uživatelé, proto nebyl obnoven a registrace byla jen odstraněna.";
        if (result.Outcome == ProcessActionOutcome.Error && result.Flags.HasFlag(ProcessActionFlags.MayHaveApplied)) text += " Změna se mohla částečně provést.";
        return text;
    }

    private static string Printable(string value) => string.Create(value.Length, value, (span, source) => {
        for (int i = 0; i < span.Length; ++i) span[i] = char.IsControl(source[i]) ? ' ' : source[i];
    });
}

public sealed partial class MainWindow
{
    private bool replacementBusy;

    private void AddReplacementMenu(MenuFlyout menu)
    {
        var item = new MenuFlyoutItem { Text = "Nahradit Správce úloh Windows…" };
        AutomationProperties.SetAutomationId(item, "taskmgr-replacement");
        item.Click += async (_, _) => await ShowReplacementAsync();
        menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(item);
    }

    private static string CurrentReplacementText()
    {
        try {
            var state = NativeReplacement.Read(Environment.ProcessPath ?? "");
            return ReplacementText.Title(state) + (state.Status is ReplacementStatus.NotRegistered || state.Error != 0 ? "" : " · " + state.Debugger);
        } catch (NativeMonitorException) { return "nelze přečíst"; }
    }

    private async Task ShowReplacementAsync()
    {
        if (replacementBusy || processActionBusy || processActionsClosed) return;
        replacementBusy = true;
        using var operation = new CancellationTokenSource(); processActionOperation = operation;
        try {
            string path = Environment.ProcessPath ?? "";
            ReplacementState? state = null;
            try { state = await Task.Run(() => NativeReplacement.Read(path)); }
            catch (NativeMonitorException error) { footer.Text = "Stav náhrady Správce úloh nelze přečíst: " + error.Message; return; }
            if (processActionsClosed) return;
            var own = model.Processes.FirstOrDefault(row => row.Key.Pid == Environment.ProcessId);
            bool identity = own is not null && model.IsCurrent(own) && !own.Stale && !own.Key.Boot.IsEmpty
                && own.Key.Kind is GenerationKind.CreationTime or GenerationKind.SequenceNumber && own.Key.Generation != 0 && own.Created100ns != 0;
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            bool installed = programFiles.Length > 0 && path.StartsWith(programFiles.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            bool trusted = PublisherTrust.IsConfigured(PublisherPolicy.Broker), readable = state.Error == 0;
            bool canEnable = trusted && identity && installed && readable && state.Status != ReplacementStatus.ThisInstall;
            bool canDisable = trusted && identity && readable && state.Status is ReplacementStatus.ThisInstall or ReplacementStatus.OtherNks;

            var content = new StackPanel { Spacing = 10, MinWidth = 320 };
            content.Children.Add(ActionText("Ctrl+Shift+Esc, Ctrl+Alt+Del → Správce úloh, hlavní panel, Win+X a Win+R taskmgr pak otevřou NKS Task Manager. "
                + "Systémový soubor se nemění, zabezpečená obrazovka Windows zůstává stejná."));
            content.Children.Add(ActionText("Stav: " + ReplacementText.Title(state), 0xe9eef0));
            content.Children.Add(ActionText(ReplacementText.Detail(state)));
            if (!trusted) content.Children.Add(ActionText(ElevationBrokerClient.Status(0xe04e0001)));
            else if (!identity) content.Children.Add(ActionText("Vlastní proces ještě není v seznamu s ověřenou identitou. Zkuste to za chvíli."));
            if (!installed) content.Children.Add(ActionText(ElevationBrokerClient.Status(0xe04e0004)));
            content.Children.Add(ActionText("Změna platí pro celý počítač a vyžaduje správce: následuje UAC a vlastní potvrzení. Po zapnutí poběží NKS s právy správce, kdykoli se Správce úloh spustí zvýšeně."));

            var dialog = ActionDialog("Nahradit Správce úloh Windows", content);
            dialog.PrimaryButtonText = "Zapnout…"; dialog.IsPrimaryButtonEnabled = canEnable;
            dialog.SecondaryButtonText = "Vypnout (obnovit předchozí)…"; dialog.IsSecondaryButtonEnabled = canDisable;
            dialog.CloseButtonText = "Zavřít"; dialog.DefaultButton = ContentDialogButton.Close;
            processActionDialog = dialog;
            var choice = await dialog.ShowAsync(); processActionDialog = null;
            if (choice == ContentDialogResult.None || processActionsClosed || own is null) return;
            await ExecuteProcessActionAsync(ActionRequest(ProcessActionCommand.TaskManager, [own], choice == ContentDialogResult.Primary ? 1ul : 2ul), [own], operation, elevated: true);
        } finally {
            processActionOperation = null; processActionDialog = null; processActionDispatched = false; replacementBusy = false;
        }
    }
}

internal static class ReplacementTextChecks
{
    internal static void Run()
    {
        ReplacementState State(ReplacementStatus status, bool backup = false, bool restorable = false, uint error = 0, string debugger = "")
            => new(status, backup, restorable, error, debugger);
        Require(ReplacementText.Title(State(ReplacementStatus.NotRegistered)) == "Nenastaveno", "Unregistered state text changed.");
        Require(ReplacementText.Title(State(ReplacementStatus.ThisInstall)) == "Nastaveno na tuto instalaci", "This-install state text changed.");
        Require(ReplacementText.Title(State(ReplacementStatus.OtherNks)).StartsWith("Nastaveno na jinou instalaci NKS", StringComparison.Ordinal), "Other-install state text changed.");
        Require(ReplacementText.Title(State(ReplacementStatus.Foreign)) == "Nastaveno na jiný program", "Foreign state text changed.");
        Require(ReplacementText.Title(State(ReplacementStatus.NotRegistered, error: 5)) == "Stav se nepodařilo přečíst", "A read failure was shown as a state.");
        Require(ReplacementText.Detail(State(ReplacementStatus.Foreign, debugger: "\"C:\\Tools\\procexp.exe\"\u0007")).Contains("Příkaz: \"C:\\Tools\\procexp.exe\" ", StringComparison.Ordinal),
            "The foreign command was not shown with control characters neutralized.");
        Require(ReplacementText.Detail(State(ReplacementStatus.ThisInstall, true, false)).Contains("jen odstraní", StringComparison.Ordinal), "An unrestorable backup was promised as restorable.");
        Require(ElevationBrokerClient.Status(0xe04e0004) == "Náhradu lze zapnout jen z instalace v Program Files."
            && ElevationBrokerClient.Status(0xe04e0005) == "Složka instalace není chráněná proti zápisu běžných uživatelů.", "Replacement refusal codes lost their text.");

        var key = new ProcessKey(new(1, 2), 100, GenerationKind.CreationTime, 7);
        var request = ProcessActionsProtocol.Request(new(ProcessActionCommand.TaskManager, [new(key, 7)], 1));
        Require(request.Length == 112 && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(6)) == 11
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(40)) == 1, "The replacement request was encoded incorrectly.");
        foreach (ulong argument in new[] { 0ul, 3ul }) {
            try { ProcessActionsProtocol.Request(new(ProcessActionCommand.TaskManager, [new(key, 7)], argument)); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("An unknown replacement option was encoded.");
        }
        Require(Answer(request, ProcessActionOutcome.Applied, ProcessActionReason.None, ProcessActionFlags.EffectConfirmed).Results[0].Outcome == ProcessActionOutcome.Applied, "A confirmed replacement change was rejected.");
        Require(Answer(request, ProcessActionOutcome.Inspected, ProcessActionReason.AlreadySame, 0).Results[0].Reason == ProcessActionReason.AlreadySame, "An unchanged replacement was rejected.");
        Require(Answer(request, ProcessActionOutcome.Refused, ProcessActionReason.OwnerMismatch, 0).Results[0].Outcome == ProcessActionOutcome.Refused, "A foreign registration answer was rejected.");
        try { Answer(request, ProcessActionOutcome.Applied, ProcessActionReason.None, 0); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("A replacement change without confirmation was accepted.");
    }

    private static ProcessActionsBatch Answer(byte[] request, ProcessActionOutcome outcome, ProcessActionReason reason, ProcessActionFlags flags)
    {
        var header = new byte[64]; request.AsSpan(0, 8).CopyTo(header); request.AsSpan(16, 24).CopyTo(header.AsSpan(16));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 112);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)ProcessActionFrame.Results);
        var row = new byte[112]; request.AsSpan(64, 40).CopyTo(row);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(48), (uint)outcome);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(52), (uint)reason);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(64), (uint)flags);
        return ProcessActionsProtocol.Response(header, row, request);
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
