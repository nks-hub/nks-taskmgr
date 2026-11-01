using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace NksTaskMgr.WinUI;

internal sealed class DumpRecoveryWindow : Window
{
    private readonly DumpRecoveryClient client = new();
    private readonly Grid root = new() { Padding = new Thickness(18), RowSpacing = 10, Background = Display.Brush(0x171d21) };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0), MaxHeight = 240 };
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12, FontFamily = UiTypography.Font };
    private readonly TextBlock status = Text("Záznamy se načtou po otevření přehledu.");
    private readonly Button refresh = Command("Obnovit přehled"), complete = Command("Dokončit uložení"), delete = Command("Odstranit dočasný soubor"),
        forget = Command("Odstranit záznam"), cancel = Command("Zrušit čekání");
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    private CancellationTokenSource? operation;
    private ContentDialog? confirmation;
    private bool loaded, busy, closed, validList;
    private DumpRecoveryEntry? Selected => (list.SelectedItem as ListViewItem)?.Tag as DumpRecoveryEntry;

    internal DumpRecoveryWindow()
    {
        Title = "Nedokončené dumpy · NKS Task Manager"; AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico")); AppWindow.Resize(new SizeInt32(1040, 820));
        Appearance.Attach(this, root);
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(2, GridUnitType.Star), new GridLength(3, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        root.Children.Add(Text("Obnova nic nemaže automaticky. Vyberte záznam; před změnou se znovu ověří soubor i původní aplikace a pracovník."));
        var commands = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemHeight = 42, ItemWidth = 210, MaximumRowsOrColumns = 4 };
        foreach (var button in new[] { refresh, complete, delete, forget, cancel }) commands.Children.Add(button);
        Grid.SetRow(commands, 1); root.Children.Add(commands); Grid.SetRow(list, 2); root.Children.Add(list);
        Grid.SetRow(detail, 3); root.Children.Add(detail); Grid.SetRow(status, 4); root.Children.Add(status); Content = root;
        AutomationProperties.SetName(list, "Omezený přehled nedokončených dumpů"); AutomationProperties.SetName(detail, "Cesty a ověřený stav vybraného záznamu obnovy");
        foreach (var button in new[] { refresh, complete, delete, forget, cancel }) AutomationProperties.SetName(button, button.Content?.ToString() ?? "");
        list.SelectionChanged += (_, _) => { ShowSelection(); Commands(); };
        refresh.Click += async (_, _) => await LoadAsync(); complete.Click += async (_, _) => await ActAsync(DumpRecoveryAction.Complete);
        delete.Click += async (_, _) => await ActAsync(DumpRecoveryAction.DeleteTemporary); forget.Click += async (_, _) => await ActAsync(DumpRecoveryAction.RemoveJournal);
        cancel.Click += (_, _) => operation?.Cancel();
        Activated += async (_, _) => { if (!loaded) { loaded = true; await LoadAsync(); } };
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(250); timer.Tick += (_, _) => Commands(); timer.Start();
        Closed += (_, _) => { closed = true; timer.Stop(); operation?.Cancel(); confirmation?.Hide(); client.Dispose(); };
        Commands();
    }
    private async Task LoadAsync()
    {
        if (busy || client.IsBusy || closed) return;
        busy = true; validList = false; using var stop = new CancellationTokenSource(); operation = stop; Commands(); status.Text = "Čtu omezený přehled…";
        try { Render(await client.LoadAsync(stop.Token)); status.Text = list.Items.Count == 0 ? "Žádné nedokončené transakce nejsou zaznamenané." : "Vyberte záznam. Neověřitelné soubory zůstanou zachované."; }
        catch (Exception error) when (Expected(error)) { if (!closed) status.Text = error is OperationCanceledException ? "Čtení bylo zrušeno." : error.Message; }
        finally { busy = false; operation = null; if (!closed) Commands(); }
    }
    private void Render(DumpRecoveryEntry[] entries)
    {
        if (closed) return;
        list.Items.Clear();
        foreach (var entry in entries) list.Items.Add(new ListViewItem { Content = Text(entry.ToString()), Tag = entry });
        validList = true; if (list.Items.Count > 0) list.SelectedIndex = 0; else detail.Text = "";
    }
    private void ShowSelection()
    {
        if (Selected is not { } entry) { detail.Text = ""; return; }
        if (entry.Document.Record is not { } record) { detail.Text = entry.Status; return; }
        detail.Text = entry.Status + "\n\nCílový soubor: " + DisplayPath(record.Destination) + "\nDočasný soubor: " + DisplayPath(record.Temporary)
            + "\nZahájeno: " + record.Started.LocalDateTime.ToString("g") + "\nZapsaná fáze: " + (record.Phase switch {
                DumpRecoveryPhase.Intent => "záměr před vytvořením souboru", DumpRecoveryPhase.TempOpened => "dočasný soubor vytvořen", DumpRecoveryPhase.StartingHelper => "spouštění pracovníka",
                DumpRecoveryPhase.WorkerReady => "pracovník před odesláním požadavku", _ => "hotová data před publikací"
            }) + "\nPůvodní aplikace PID " + record.Creator.Pid + " · pracovník " + (record.Helper?.Pid.ToString() ?? "identita nebyla zapsaná")
            + "\nZjištěná velikost: " + Display.Number(entry.Bytes / 1048576d, " MiB", 2)
            + (record.Phase == DumpRecoveryPhase.Ready ? "\nOčekávaná velikost: " + record.FileBytes + " B\nZapsané SHA-256: " + record.Sha256 : "");
    }
    private void Commands()
    {
        if (closed) return;
        bool available = !busy && !client.IsBusy && validList;
        refresh.IsEnabled = !busy && !client.IsBusy; cancel.IsEnabled = operation is not null;
        complete.IsEnabled = available && Selected?.State is DumpRecoveryState.Ready or DumpRecoveryState.Published;
        delete.IsEnabled = available && Selected?.State is DumpRecoveryState.Partial or DumpRecoveryState.Ready;
        forget.IsEnabled = available && Selected?.State == DumpRecoveryState.Missing;
        complete.Content = Selected?.State == DumpRecoveryState.Published ? "Ověřit hotový záznam" : "Dokončit uložení";
    }
    private async Task ActAsync(DumpRecoveryAction action)
    {
        if (busy || client.IsBusy || !validList || Selected is not { Document.Record: { } record } entry) return;
        string message = action switch {
            DumpRecoveryAction.DeleteTemporary => "Odstraní pouze ověřený dočasný soubor. Jeho obsah paměti bude ztracen.\n\n" + DisplayPath(record.Temporary),
            DumpRecoveryAction.RemoveJournal => "Znovu ověří nepřítomnost dočasného i cílového souboru a odstraní pouze záznam této transakce.",
            _ when entry.State == DumpRecoveryState.Published => "Ověří velikost a SHA-256 již uloženého souboru. Potom odstraní pouze dokončený pomocný záznam. Finální dump se nemění.",
            _ => "Ověří velikost a SHA-256 hotového dočasného souboru. Potom jej uloží pod cílovým názvem bez přepsání existujících dat.\n\n" + DisplayPath(record.Destination)
        };
        var confirm = Appearance.Dialog(new ContentDialog { XamlRoot = root.XamlRoot, Title = action == DumpRecoveryAction.DeleteTemporary ? "Odstranit dočasný dump" : "Potvrdit obnovu",
            Content = Text(message), PrimaryButtonText = action == DumpRecoveryAction.DeleteTemporary ? "Odstranit" : "Pokračovat", CloseButtonText = "Ponechat", DefaultButton = ContentDialogButton.Close });
        confirmation = confirm;
        try { if (await confirm.ShowAsync() != ContentDialogResult.Primary || closed) return; }
        finally { confirmation = null; }
        busy = true; using var stop = new CancellationTokenSource(); operation = stop; Commands(); status.Text = "Ověřuji soubor a původní procesy…";
        try {
            string result = await client.ActAsync(entry, action, stop.Token);
            if (!closed) status.Text = result;
            try { Render(await client.LoadAsync(stop.Token)); }
            catch (Exception error) when (Expected(error)) { if (!closed) status.Text = result + "\nPřehled se nepodařilo znovu načíst: " + error.Message; }
        } catch (Exception error) when (Expected(error)) {
            if (!closed) status.Text = error is OperationCanceledException ? "Čekání bylo zrušeno. Výsledek není potvrzený; obnovte přehled." : error.Message;
        } finally { busy = false; operation = null; if (!closed) Commands(); }
    }
    private static bool Expected(Exception error) => error is IOException or ArgumentException or System.ComponentModel.Win32Exception or UnauthorizedAccessException
        or System.Security.SecurityException or NksTaskMgr.Native.NativeMonitorException or OperationCanceledException;
    private static string DisplayPath(string path) => path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path[4..] : path;
    private static TextBlock Text(string value) => new() { Text = value, FontFamily = UiTypography.Font, FontSize = 12, Foreground = Display.Brush(0x95a4ad), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static Button Command(string value) => new() { Content = value, FontFamily = UiTypography.Font, FontSize = 12, MinHeight = 0, Height = 35, Margin = new Thickness(0, 0, 8, 0) };
}
