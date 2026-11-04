using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed partial class ProcessDetailsView
{
    private ProcessHandleObservation? observation;
    private ProcessHandleInfo? observationHandle;
    private DateTimeOffset? observationTime;
    private readonly ulong created100ns;
    private readonly string processName;
    private readonly Func<ProcessActionsRequest, bool, CancellationToken, Task<ProcessActionsBatch>>? executeAction;

    private void ClearObservation() { observation = null; observationHandle = null; observationTime = null; }

    private bool UpdateObjectDetail()
    {
        if (mode is not (ProcessDetailsCommand.Memory or ProcessDetailsCommand.Handles)) return false;
        var text = new StringBuilder();
        if (SelectedRow is ProcessMemoryRegion region) {
            text.Append("Základní adresa: 0x").Append(region.BaseAddress.ToString("X16")).AppendLine();
            text.Append("Konec oblasti: 0x").Append((region.BaseAddress + region.RegionSize).ToString("X16")).AppendLine();
            text.Append("Velikost: ").Append(region.RegionSize.ToString("N0", Czech)).Append(" B").AppendLine();
            text.Append("Stav: ").Append(ProcessMemoryRegion.StateText(region.State)).AppendLine();
            text.Append("Typ: ").Append(ProcessMemoryRegion.TypeText(region.Type)).AppendLine();
            text.Append("Ochrana: ").Append(region.State == 0x1000 ? Protection(region.Protection) : "Pro tento stav není určena").AppendLine();
            text.Append("Základní adresa alokace: ").Append(region.State == 0x10000 ? "Volná oblast" : "0x" + region.AllocationBase.ToString("X16")).AppendLine();
            text.Append("Původní ochrana alokace: ").Append(region.State == 0x10000 ? "Volná oblast" : Protection(region.AllocationProtection));
        } else if (SelectedHandle is { } handle) {
            text.Append("Hodnota handle: 0x").Append(handle.Value.ToString("X")).AppendLine();
            text.Append("Přístup ve snímku: 0x").Append(handle.GrantedAccess.ToString("X8")).AppendLine();
            text.Append("Index typu ve snímku: ").Append(handle.TypeIndex.ToString(Czech)).AppendLine();
            text.Append("Atributy ve snímku: 0x").Append(handle.Attributes.ToString("X8")).AppendLine().AppendLine();
            if (observation is not null && ReferenceEquals(observationHandle, handle)) {
                text.Append("Aktuálně zachycený typ: ").Append(ObjectText(observation.Type, observation.TypeState, observation.TypeNtStatus, false)).AppendLine();
                text.Append("Aktuálně zachycený název: ").Append(ObjectText(observation.Name, observation.NameState, observation.NameNtStatus, true)).AppendLine();
                if (observation.Fingerprint.Present) text.Append("Otisk objektu: ").Append(observation.Fingerprint.Text).AppendLine();
                text.Append("Pozorováno: ").Append(observationTime?.LocalDateTime.ToString("g", Czech)).AppendLine();
            } else text.AppendLine("Typ a název objektu zatím nebyly dotázané.");
            text.AppendLine().Append("Dotaz zachytí objekt aktuálně duplikovaný pod touto hodnotou. Po uzavření a opětovném použití handle může jít o jiný objekt než ve snímku.");
        } else text.Append("Vyberte řádek ze seznamu.");
        detail.Text = text.ToString(); return true;
    }

    private static string ObjectText(string value, SampleState state, int status, bool name)
        => state == SampleState.Valid ? value.Length == 0 ? name ? "Nepojmenovaný objekt" : "Prázdný typ" : value
            : MetricState(state, 0) + (status == 0 ? "" : " (NT 0x" + unchecked((uint)status).ToString("X8") + ")");

    private static string Protection(uint value)
    {
        string basis = (value & 0xff) switch {
            1 => "Bez přístupu", 2 => "Čtení", 4 => "Čtení / zápis", 8 => "Kopie při zápisu",
            0x10 => "Spuštění", 0x20 => "Spuštění / čtení", 0x40 => "Spuštění / čtení / zápis", 0x80 => "Spuštění / kopie při zápisu", _ => "Neuvedena"
        };
        if ((value & 0x100) != 0) basis += " · GUARD";
        if ((value & 0x200) != 0) basis += " · NOCACHE";
        if ((value & 0x400) != 0) basis += " · WRITECOMBINE";
        return basis + " (0x" + value.ToString("X8") + ")";
    }

    private void ConfigureHandleClose() => list.ContextRequested += (_, args) => {
        if (mode != ProcessDetailsCommand.Handles || args.Handled) return;
        bool pointer = args.TryGetPosition(list, out var point);
        var source = pointer ? args.OriginalSource as DependencyObject : FocusManager.GetFocusedElement(list.XamlRoot) as DependencyObject;
        if (ContextListSelection.FromSource(list, source, !pointer) is not ProcessDetailsRow { Source: ProcessHandleInfo handle }) return;
        args.Handled = true;
        bool enabled = executeAction is not null && operation is null && !targetChanged && isCurrent();
        var menu = new MenuFlyout();
        foreach (bool elevated in new[] { false, true }) {
            var item = new MenuFlyoutItem { Text = elevated ? "Zavřít handle jako správce…" : "Zavřít handle v procesu…", IsEnabled = enabled };
            Identify(item, elevated ? "process-details-close-handle-elevated" : "process-details-close-handle",
                elevated ? "Zavřít vybraný handle v cílovém procesu se zvýšenými právy" : "Zavřít vybraný handle v cílovém procesu");
            item.Click += async (_, _) => await CloseHandleAsync(handle, elevated);
            if (elevated) menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(item);
        }
        if (pointer) menu.ShowAt(list, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(list.ContainerFromItem(list.SelectedItem) as FrameworkElement ?? list);
    };

    private async Task CloseHandleAsync(ProcessHandleInfo handle, bool elevated)
    {
        if (executeAction is null || closed || operation is not null) return;
        // The dialog names the object observed under this value right now; the worker rechecks it before closing.
        await ReadAsync(ProcessDetailsCommand.HandleInfo, handle: handle);
        if (closed || targetChanged || !isCurrent() || !ReferenceEquals(observationHandle, handle) || observation is not { } seen) return;
        if (seen.TypeState != SampleState.Valid) { state.Text = "Typ objektu nelze ověřit, proto handle nelze bezpečně zavřít."; return; }
        if (seen.Type == "File" && seen.NameState != SampleState.Valid) { state.Text = "Název souboru nelze přečíst, proto handle nelze bezpečně zavřít."; return; }
        var spec = new ProcessHandleSpec(handle.Value, handle.GrantedAccess, handle.TypeIndex, seen.Type,
            seen.NameState == SampleState.Valid ? seen.Name : null, seen.Fingerprint);
        if (!await ConfirmHandleCloseAsync(spec, elevated) || closed) return;
        if (targetChanged || !isCurrent() || operation is not null) { state.Text = "Proces už není aktuální. Handle nebyl zavřen."; return; }
        using var current = new CancellationTokenSource(); operation = current; UpdateAvailability();
        state.Text = elevated ? "Čekám na UAC, potvrzení správce a výsledek…" : "Zavírám handle a čekám na výsledek pracovníka…";
        string result; bool confirmed = false;
        try {
            var batch = await executeAction(new ProcessActionsRequest(ProcessActionCommand.CloseHandle,
                [new ProcessActionTarget(Key, created100ns)], Handle: spec), elevated, current.Token);
            string broker = ElevationBrokerClient.Status(batch.Error);
            result = (broker.Length > 0 ? broker + "\n" : "") + (batch.Frame == ProcessActionFrame.Results && batch.Results.Length == 1
                ? MainWindow.ActionResultText(batch.Results[0], ProcessActionCommand.CloseHandle)
                : batch.Accepted ? "Výsledek není známý. Handle mohl být zavřen." : "Pracovník požadavek nepřijal. Handle nebyl zavřen.");
            confirmed = batch.Results is [{ Outcome: ProcessActionOutcome.Applied }];
        } catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or OperationCanceledException or ArgumentException) {
            result = "Výsledek není známý. Handle mohl být zavřen. " + (error is OperationCanceledException ? "Čekání bylo zrušeno." : error.Message);
        } finally { operation = null; if (!closed) UpdateAvailability(); }
        if (closed) return;
        state.Text = result.Replace('\n', ' ');
        await HandleDialog("Výsledek zavření handle", HandleSummary(spec) + "\n\n" + result).ShowAsync();
        if (confirmed && !closed) await ReadAsync(ProcessDetailsCommand.Handles);
    }

    private async Task<bool> ConfirmHandleCloseAsync(ProcessHandleSpec spec, bool elevated)
    {
        var acknowledge = new CheckBox { Content = "Rozumím, že proces může spadnout nebo přijít o data.", FontSize = 12 };
        var dialog = HandleDialog("Zavřít handle v cizím procesu", HandleSummary(spec) + "\n\n"
            + "Proces o zavření neví a s handlem může dál počítat. Může spadnout, přestat fungovat nebo ztratit či poškodit neuložená data. Zavření nelze vrátit.\n\n"
            + "Těsně před zavřením se znovu ověří identita procesu a to, že pod hodnotou je objekt stejného typu a přístupu"
            + (spec.Name is { Length: > 0 } ? ", se stejným názvem" : "") + (spec.Fingerprint.Present ? ", se stejným otiskem" : "") + ". Pokud se cokoli změnilo, zavření se odmítne."
            + (spec.Name is { Length: > 0 } || spec.Fingerprint.Present ? "" : " Objekt nemá název ani otisk, takže jeho totožnost s tímto řádkem prokázat nelze; ověří se jen typ a přístup.")
            + (elevated ? "\n\nNásleduje UAC a vlastní potvrzení správce. Nepotvrzený požadavek se neopakuje." : ""), acknowledge);
        dialog.PrimaryButtonText = "Zavřít handle"; dialog.CloseButtonText = "Zrušit"; dialog.IsPrimaryButtonEnabled = false;
        acknowledge.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        acknowledge.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        return await dialog.ShowAsync() == ContentDialogResult.Primary && acknowledge.IsChecked == true;
    }

    private string HandleSummary(ProcessHandleSpec spec) => "Proces: " + processName + " · PID " + Key.Pid.ToString(Czech)
        + "\nHandle: 0x" + spec.Value.ToString("X") + " · přístup 0x" + spec.GrantedAccess.ToString("X8")
        + "\nObjekt: " + spec.Type + " · " + (spec.Name is null ? "název nelze ověřit" : spec.Name.Length == 0 ? "nepojmenovaný" : Flat(spec.Name))
        + (spec.Fingerprint.Present ? "\nOtisk: " + spec.Fingerprint.Text : "");

    private ContentDialog HandleDialog(string heading, string text, UIElement? extra = null)
    {
        var content = new StackPanel { Spacing = 10, MinWidth = 320 };
        content.Children.Add(new TextBlock { Text = text, FontFamily = UiTypography.Font, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true, Foreground = Display.Brush(0xe9eef0) });
        if (extra is not null) content.Children.Add(extra);
        return Appearance.Dialog(new ContentDialog { Title = heading, XamlRoot = layout.XamlRoot, CloseButtonText = "Zavřít",
            DefaultButton = ContentDialogButton.Close, Content = new ScrollViewer { Content = content, MaxHeight = 460,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
    }
}
