using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics;

namespace NksTaskMgr.WinUI;

internal sealed partial class UpdateWindow : Window
{
    private readonly Grid root = new() { Padding = new Thickness(22), RowSpacing = 14, Background = Display.Brush(0x171d21) };
    private readonly TextBlock status = Text("Načítání…"), result = Text(""), location = Text(""), recoveryDetail = Text("");
    private readonly TextBlock emptyTitle = Text("Ověřuji dostupnost aktualizací", 17), emptyMessage = Text("Načítám přehled místních instalačních balíčků.");
    private readonly TextBlock manualUpdate = Text("Novou verzi nainstalujte ručně jejím instalátorem po zavření aplikace.");
    private readonly Border emptyState = new() { Padding = new Thickness(18), CornerRadius = new CornerRadius(8),
        Background = Display.Brush(0x1d252a), BorderBrush = Display.Brush(0x303b42), BorderThickness = new Thickness(1) };
    private readonly StackPanel recoveryPanel = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly ComboBox recoveryChoices = new() { HorizontalAlignment = HorizontalAlignment.Stretch, FontFamily = UiTypography.Font, FontSize = 13 };
    private readonly ListView packages = new() { MinHeight = 100, MaxHeight = 240, SelectionMode = ListViewSelectionMode.Single,
        FontFamily = UiTypography.Font, FontSize = 13, Visibility = Visibility.Collapsed };
    private readonly Button refresh = Command("Načíst balíčky"), add = Command("Přidat instalátor…"), cleanup = Command("Vyčistit cache…"), install = Command("Nainstalovat vybraný…"), rollback = Command("Vrátit předchozí verzi…"), repair = Command("Obnovit nedokončenou instalaci…"), cancel = Command("Zrušit dotaz"), close = Command("Zavřít");
    private readonly UpdateWorkerClient client = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Action requestShutdown;
    private CancellationTokenSource? operation;
    private UpdateSession? prepared;
    private UpdateCleanupSession? preparedCleanup;
    private ContentDialog? confirmation;
    private UpdateInventory? inventory;
    private bool opened, closed, busy, handoffUncertain, cleanupUncertain;

    internal UpdateWindow(Action requestShutdown)
    {
        this.requestShutdown = requestShutdown;
        Title = "Aktualizace · NKS Task Manager";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.Resize(UpdateWorkerClient.IsConfigured ? new SizeInt32(920, 690) : new SizeInt32(640, 440));
        Appearance.Attach(this, root);
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        var heading = new StackPanel { Spacing = 7 };
        heading.Children.Add(Text("Aktualizace a návrat verze", 23));
        heading.Children.Add(Text("Aktuální sestavení: " + CurrentVersion()));
        heading.Children.Add(Text(UpdateWorkerClient.IsConfigured
            ? "Vyberte podepsaný instalátor. Ověřená instalace i přenosná kopie zachovají svůj režim. Nastavení mimo soubory aplikace se nepřepisuje."
            : StatusText(UpdateStatus.TrustMissing) + " Balíčky se bez něj neověřují ani nespouštějí."));
        heading.Children.Add(location);
        root.Children.Add(heading);
        Grid.SetRow(status, 1); root.Children.Add(status);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(refresh); tools.Children.Add(add); tools.Children.Add(cleanup); tools.Children.Add(cancel);
        Grid.SetRow(tools, 2); root.Children.Add(tools);
        Grid.SetRow(packages, 3); root.Children.Add(packages);
        var emptyContent = new StackPanel { Spacing = 10 };
        emptyTitle.Foreground = Display.Brush(0xe9eef0); emptyContent.Children.Add(emptyTitle); emptyContent.Children.Add(emptyMessage);
        manualUpdate.Visibility = Visibility.Collapsed; emptyContent.Children.Add(manualUpdate); emptyState.Child = emptyContent;
        Grid.SetRow(emptyState, 3); root.Children.Add(emptyState);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(install); actions.Children.Add(rollback); Grid.SetRow(actions, 4); root.Children.Add(actions);
        root.SizeChanged += (_, args) => {
            tools.Orientation = actions.Orientation = args.NewSize.Width < 560 ? Orientation.Vertical : Orientation.Horizontal;
        };
        recoveryPanel.Children.Add(Text("Nedokončené instalace", 16));
        recoveryPanel.Children.Add(recoveryChoices); recoveryPanel.Children.Add(recoveryDetail); recoveryPanel.Children.Add(repair);
        var outcomes = new StackPanel { Spacing = 12 }; outcomes.Children.Add(recoveryPanel); outcomes.Children.Add(result);
        Grid.SetRow(outcomes, 5); root.Children.Add(outcomes);
        close.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetRow(close, 6); root.Children.Add(close);
        AutomationProperties.SetName(packages, "Ověřené instalační balíčky v místní cache");
        AutomationProperties.SetAutomationId(packages, "update-packages");
        AutomationProperties.SetAutomationId(add, "update-add-installer");
        AutomationProperties.SetAutomationId(install, "update-install-selected");
        AutomationProperties.SetAutomationId(rollback, "update-rollback");
        AutomationProperties.SetAutomationId(cleanup, "update-cleanup");
        AutomationProperties.SetAutomationId(emptyState, "update-empty-state");
        AutomationProperties.SetAutomationId(close, "update-close");
        ToolTipService.SetToolTip(cleanup, "Nejdříve zobrazí náhled odstranitelné cache. Balíčky potřebné pro návrat a nejasné záznamy zůstanou zachované.");
        AutomationProperties.SetName(recoveryChoices, "Nejasné výsledky instalace v této složce");
        AutomationProperties.SetAutomationId(repair, "update-repair");
        refresh.Click += async (_, _) => await RunAsync(ReadInventoryAsync);
        add.Click += async (_, _) => await RunAsync(AddInstallerAsync);
        cleanup.Click += async (_, _) => await RunAsync(CleanupAsync);
        install.Click += async (_, _) => { if (SelectedPackage is { } package) await RunAsync(token => PrepareAsync(package.Sha256, token)); };
        rollback.Click += async (_, _) => await RunAsync(token => PrepareAsync(null, token));
        repair.Click += async (_, _) => {
            if (SelectedRecovery is { CanRepair: true } candidate)
                await RunAsync(token => PrepareAsync(null, token, candidate.CandidateId));
        };
        recoveryChoices.SelectionChanged += (_, _) => {
            recoveryDetail.Text = SelectedRecovery is { } candidate ? RecoveryReason(candidate) : "";
            RefreshButtons();
        };
        cancel.Click += (_, _) => operation?.Cancel();
        close.Click += (_, _) => Close();
        packages.SelectionChanged += (_, _) => RefreshButtons();
        timer.Tick += (_, _) => RefreshButtons();
        Activated += async (_, _) => {
            if (opened) return;
            opened = true;
            if (!UpdateWorkerClient.IsConfigured) {
                ShowUnavailable("Toto sestavení používá ruční aktualizace.",
                    "Aktualizace přímo z aplikace nejsou pro toto sestavení dostupné.", true);
                status.Text = "Ruční aktualizace"; RefreshButtons(); return;
            }
            timer.Start(); await RunAsync(ReadInventoryAsync);
        };
        Closed += (_, _) => { closed = true; operation?.Cancel(); confirmation?.Hide(); timer.Stop(); client.Dispose(); };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        RefreshButtons();
    }

    private UpdatePackage? SelectedPackage => (packages.SelectedItem as ListViewItem)?.Tag as UpdatePackage;
    private UpdateRecoveryCandidate? SelectedRecovery => (recoveryChoices.SelectedItem as ComboBoxItem)?.Tag as UpdateRecoveryCandidate;

    internal void ShowOwned(Window owner)
    {
        ObjectDisposedException.ThrowIf(closed, this);
        WindowOwnership.Set(this, owner); WindowOwnership.Restore(this); Activate();
    }

    private void ShowUnavailable(string title, string message, bool manual)
    {
        inventory = null; packages.ItemsSource = null; recoveryChoices.ItemsSource = null; recoveryPanel.Visibility = Visibility.Collapsed;
        emptyTitle.Text = title; emptyMessage.Text = message; emptyState.Visibility = Visibility.Visible; packages.Visibility = Visibility.Collapsed;
        manualUpdate.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        location.Text = "Složka aplikace: " + AppContext.BaseDirectory;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (closed || busy || client.IsBusy || handoffUncertain || !UpdateWorkerClient.IsConfigured) return;
        busy = true;
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        result.Text = ""; RefreshButtons();
        try { await action(cancellation.Token); }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or Win32Exception or UnauthorizedAccessException
            or CryptographicException or COMException or InvalidOperationException or NksTaskMgr.Native.NativeMonitorException) {
            if (prepared?.CommitSent == true) handoffUncertain = true;
            if (preparedCleanup?.CommitSent == true) cleanupUncertain = true;
            if (!closed) {
                ShowUnavailable(error is CryptographicException || error is UpdateException { Status: UpdateStatus.TrustMissing or UpdateStatus.Rejected }
                    ? "Aktualizace přes aplikaci není dostupná." : "Přehled balíčků se nepodařilo načíst.",
                    handoffUncertain ? "Výsledek předání není potvrzený. Neopakujte instalaci, dokud neověříte její stav."
                        : cleanupUncertain ? "Výsledek úklidu není potvrzený. Po skončení pomocníka načtěte přehled znovu."
                        : error is OperationCanceledException ? "Načítání bylo zrušené. Přehled můžete načíst znovu."
                        : "Operace nebyla dokončená. Podrobnosti jsou uvedené níže; načtení můžete zopakovat.", !handoffUncertain && !cleanupUncertain);
                status.Text = handoffUncertain ? "Předání nemá potvrzený výsledek."
                    : cleanupUncertain ? "Úklid nemá potvrzený výsledek."
                    : error is UpdateException nativeError ? StatusText(nativeError.Status)
                    : error is OperationCanceledException ? "Operace byla zrušena." : "Operaci nelze dokončit.";
                result.Text = handoffUncertain
                    ? "Aplikace zůstává otevřená. Neopakujte instalaci; po skončení pomocníka znovu otevřete přehled a ověřte jeho stav."
                    : cleanupUncertain ? "Část souborů už mohla být odstraněná. Po skončení pomocníka načtěte aktuální přehled; úklid se neopakuje automaticky."
                    : error is OperationCanceledException ? "" : Bound(error.Message);
            }
        } finally {
            prepared = null; preparedCleanup = null; operation = null; busy = false;
            if (!closed) RefreshButtons();
        }
    }

    private async Task ReadInventoryAsync(CancellationToken token)
    {
        status.Text = "Ověřuji místní balíčky…";
        var value = await client.ListAsync(token);
        if (closed) return;
        ShowInventory(value);
        status.Text = StatusText(value.Status) + "\n" + JournalText(value.JournalState);
        result.Text = value.Status == UpdateStatus.Ok ? "Nová instalace začne až po kontrole plánu, vašem potvrzení a skutečném zavření této aplikace." : Bound(value.Detail);
    }

    private async Task AddInstallerAsync(CancellationToken token)
    {
        var picker = new FileOpenPicker(AppWindow.Id); picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is null || closed || token.IsCancellationRequested) return;
        status.Text = "Ověřuji a ukládám vybraný instalátor…";
        var value = await client.CacheAsync(file.Path, token);
        if (closed) return;
        ShowInventory(value);
        status.Text = StatusText(value.Status) + "\n" + JournalText(value.JournalState);
        result.Text = value.Status == UpdateStatus.Ok ? "Instalátor je uložený. Samotné přidání balíčku nic neinstaluje." : Bound(value.Detail);
    }

    private void ShowInventory(UpdateInventory value)
    {
        inventory = value;
        cleanupUncertain = false;
        packages.ItemsSource = value.Packages.Select(package => new ListViewItem { Tag = package,
            Content = VersionLabel(package.Version) + " · " + Display.Number(package.Bytes / 1048576d, " MiB", 1)
                + (package.Flags.HasFlag(UpdatePackageFlags.Current) ? " · současná verze" : package.Flags.HasFlag(UpdatePackageFlags.Previous) ? " · předchozí verze" : " · připravený balíček") }).ToArray();
        bool empty = value.Packages.Length == 0;
        packages.Visibility = empty ? Visibility.Collapsed : Visibility.Visible; emptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        emptyTitle.Text = value.Status == UpdateStatus.Ok ? "Žádné místní instalační balíčky" : "Přehled balíčků není dostupný";
        emptyMessage.Text = value.Status == UpdateStatus.Ok
            ? "Přidejte instalátor od nastaveného vydavatele. Nejdříve se ověří; samotné přidání nic neinstaluje."
            : StatusText(value.Status);
        manualUpdate.Visibility = value.Status == UpdateStatus.Ok ? Visibility.Collapsed : Visibility.Visible;
        location.Text = (value.Mode switch { UpdateInstallMode.Installed => "Registrovaná instalace", UpdateInstallMode.Portable => "Přenosná kopie", _ => "Režim složky není ověřený" })
            + (value.InstallDirectory.Length == 0 ? "" : " · " + value.InstallDirectory);
        if (value.CompatibilityDetail.Length > 0) location.Text += "\n" + Bound(value.CompatibilityDetail);
        recoveryChoices.ItemsSource = value.RecoveryCandidates.Select(candidate => new ComboBoxItem { Tag = candidate,
            Content = VersionLabel(candidate.OriginalVersion) + " · " + (candidate.CanRepair ? "lze připravit obnovu" : "obnova zatím není ověřená") }).ToArray();
        recoveryPanel.Visibility = value.RecoveryCandidates.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        recoveryDetail.Text = "";
        if (value.RecoveryCandidates.Length > 0) recoveryChoices.SelectedIndex = 0;
    }

    private async Task PrepareAsync(string? packageSha256, CancellationToken token, string? recoveryCandidateId = null)
    {
        status.Text = "Připravuji ověřený plán…";
        using var session = recoveryCandidateId is null ? await client.PrepareAsync(packageSha256, token)
            : await client.PrepareRecoveryAsync(recoveryCandidateId, token);
        prepared = session;
        if (closed || token.IsCancellationRequested) return;
        var plan = session.Plan;
        var content = new StackPanel { Spacing = 12, MinWidth = 320, MaxWidth = 620 };
        content.Children.Add(Text("Současná verze: " + VersionLabel(plan.CurrentVersion)));
        content.Children.Add(Text("Vybraná verze: " + VersionLabel(plan.SelectedVersion)));
        if (!plan.IsRecovery) content.Children.Add(Text("Uložený návrat po změně: " + VersionLabel(plan.PreviousVersion)));
        content.Children.Add(Text("Cílová instalace: " + plan.InstallDirectory));
        content.Children.Add(Text(plan.Mode == UpdateInstallMode.Portable
            ? "Zůstane přenosná kopie. Nevytváří se registrace odinstalátoru ani zástupci aplikace."
            : "Zůstane instalace pro aktuálního uživatele."));
        if (plan.IsRecovery) content.Children.Add(Text("Znovu se nainstaluje ověřená původní verze. Dřívější nejasný výsledek se považuje za vyřešený až po dokončení a ověření nové instalace."));
        content.Children.Add(Text("Po potvrzení se tento program zavře a otevře se ověřený instalátor. Pomocník vyčká na skutečný zánik procesu. Pokud program zůstane otevřený, instalace se po 30 sekundách nespustí. Nastavení mimo instalační složku se nepřepisuje."));
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = plan.IsRecovery ? "Obnovit nedokončenou instalaci" : plan.Action == UpdateAction.Rollback ? "Vrátit předchozí verzi" : "Nainstalovat aktualizaci",
            Content = content, PrimaryButtonText = "Zavřít program a pokračovat", CloseButtonText = "Zrušit", DefaultButton = ContentDialogButton.Close };
        Appearance.Dialog(dialog); confirmation = dialog;
        dialog.PrimaryButtonClick += (_, args) => { if (token.IsCancellationRequested || UpdateProtocol.NowMs >= session.CommitDeadlineMonotonicMs) args.Cancel = true; };
        RefreshButtons();
        ContentDialogResult choice;
        try { choice = await dialog.ShowAsync(); }
        finally { confirmation = null; }
        if (choice != ContentDialogResult.Primary || closed || token.IsCancellationRequested) { status.Text = "Instalace nebyla předaná."; return; }
        var handoff = await session.CommitAsync(token);
        if (closed) return;
        if (handoff.Accepted) {
            handoffUncertain = true;
            status.Text = "Instalátor čeká na uzavření aplikace…";
            result.Text = "Předání je potvrzené. Případné odmítnutí zavření ponechá aplikaci otevřenou; pomocník potom ukončí čekání bez spuštění instalace.";
            RefreshButtons(); requestShutdown();
        } else {
            handoffUncertain = handoff.MayHaveCommitted;
            status.Text = StatusText(handoff.Status);
            result.Text = handoff.MayHaveCommitted ? "Výsledek předání není známý. Instalaci neopakujte, dokud neověříte stav předchozího požadavku." : Bound(handoff.Detail);
        }
    }

    private void RefreshButtons()
    {
        bool idle = !closed && !busy && !client.IsBusy && !handoffUncertain;
        bool trusted = idle && inventory?.Status is UpdateStatus.Ok or UpdateStatus.Conflict;
        bool ready = trusted && inventory!.StandardOperationsAllowed;
        bool hasInventory = inventory?.Status is UpdateStatus.Ok or UpdateStatus.Conflict;
        refresh.IsEnabled = idle && UpdateWorkerClient.IsConfigured; add.IsEnabled = trusted; cleanup.IsEnabled = trusted;
        refresh.Visibility = UpdateWorkerClient.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        add.Visibility = cleanup.Visibility = hasInventory ? Visibility.Visible : Visibility.Collapsed;
        install.Visibility = rollback.Visibility = hasInventory && inventory!.Packages.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        install.IsEnabled = ready && SelectedPackage is { } package && !package.Flags.HasFlag(UpdatePackageFlags.Current);
        rollback.IsEnabled = ready && inventory!.Packages.Any(package => package.Flags.HasFlag(UpdatePackageFlags.Previous));
        recoveryChoices.IsEnabled = idle;
        repair.IsEnabled = trusted && SelectedRecovery is { CanRepair: true };
        cancel.IsEnabled = busy && prepared?.CommitSent != true && preparedCleanup?.CommitSent != true;
        if (confirmation is { } dialog && prepared is { } session)
            dialog.IsPrimaryButtonEnabled = !closed && operation?.IsCancellationRequested != true && UpdateProtocol.NowMs < session.CommitDeadlineMonotonicMs;
        if (confirmation is { } cleanupDialog && preparedCleanup is { } cleanupSession) {
            ulong now = UpdateProtocol.NowMs, deadline = cleanupSession.CommitDeadlineMonotonicMs;
            cleanupDialog.IsPrimaryButtonEnabled = !closed && operation?.IsCancellationRequested != true && now < deadline;
            if (cleanupDeadline is { } label) label.Text = now < deadline
                ? "Náhled platí ještě " + ((deadline - now + 999) / 1000) + " s."
                : "Platnost náhledu vypršela. Zavřete dialog a připravte nový náhled.";
        }
    }

    private static string StatusText(UpdateStatus value) => value switch {
        UpdateStatus.Ok => "Místní balíčky jsou ověřené.", UpdateStatus.TrustMissing => "Toto sestavení nemá nastaveného důvěryhodného vydavatele aktualizací.",
        UpdateStatus.Rejected => "Původ balíčku nebo instalace nebyl ověřen.", UpdateStatus.InvalidRequest => "Požadavek neodpovídá připravenému plánu.",
        UpdateStatus.CacheFull => "Cache dosáhla svého limitu.", UpdateStatus.Conflict => "Předchozí operace nebo stav instalace brání pokračování.",
        UpdateStatus.NotFound => "Chybí požadovaný ověřený balíček. Pro návrat je nutný také instalátor současné verze.",
        UpdateStatus.Io => "Soubor nebo záznam aktualizace není dostupný.", UpdateStatus.GuiAlive => "Program zůstal otevřený; instalace nebyla spuštěná.",
        UpdateStatus.InstallerFailed => "Instalátor neskončil úspěšně.", UpdateStatus.VerificationFailed => "Výsledek instalace nebyl ověřený.",
        UpdateStatus.Busy => "Předchozí aktualizační požadavek ještě není uzavřený.", _ => "Výsledek předchozí operace není známý."
    };
    private static string JournalText(UpdateJournalState value) => value switch {
        UpdateJournalState.None => "Zatím není zaznamenaná žádná aktualizace.", UpdateJournalState.Cached => "Instalátor je připravený v cache.",
        UpdateJournalState.Planned => "Existuje připravený plán.", UpdateJournalState.Committed or UpdateJournalState.WaitingGui => "Pomocník čeká na uzavření programu.",
        UpdateJournalState.Installing => "Instalace nemá potvrzený konečný výsledek.", UpdateJournalState.Verified => "Poslední změna verze byla ověřená.",
        UpdateJournalState.Rejected => "Předchozí požadavek byl odmítnutý.", UpdateJournalState.GuiAlive => "Program zůstal otevřený a čekání skončilo.",
        UpdateJournalState.InstallerFailed => "Poslední instalace selhala.", UpdateJournalState.VerificationFailed => "Poslední výsledek nebyl ověřený.",
        _ => "Předchozí stav vyžaduje ověření; instalace se neopakuje automaticky."
    };
    private static string VersionLabel(string version) {
        if (string.IsNullOrWhiteSpace(version)) return "Neověřená verze";
        int plus = version.IndexOf('+');
        return plus >= 0 && version.Length > plus + 8 ? version[..plus] + " · " + version.Substring(plus + 1, 7) : version;
    }
    private static string RecoveryReason(UpdateRecoveryCandidate candidate) => candidate.Safety switch {
        UpdateRecoverySafety.Ready => "Lze připravit opravu z ověřeného původního instalátoru. Samotný výběr nic neinstaluje.",
        UpdateRecoverySafety.WorkerAlive => "Původní aktualizační pomocník ještě běží. Obnova zatím není povolená.",
        UpdateRecoverySafety.InstallerAlive => "Původní instalátor ještě běží. Vyčkejte na jeho ukončení.",
        UpdateRecoverySafety.LifetimeUnknown => "Nelze ověřit, zda předchozí instalátor skončil. Soubory zůstávají zachované.",
        UpdateRecoverySafety.OriginalSetupMissing => "Chybí ověřený instalátor původní verze. Přidejte jej do místních balíčků a obnovte přehled.",
        UpdateRecoverySafety.ScopeMismatch => "Záznam není ověřený pro tuto složku aplikace.",
        UpdateRecoverySafety.Resolved => "Tuto operaci už vyřešila jiná ověřená obnova.",
        UpdateRecoverySafety.LegacySameBoot => "Starý záznam neobsahuje životnost instalátoru. V tomto spuštění Windows nelze prokázat jeho ukončení.",
        _ => Bound(candidate.Detail)
    };
    private static string CurrentVersion() {
        try { return VersionLabel(FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion ?? "Neznámá verze"); }
        catch (Exception error) when (error is IOException or Win32Exception) { return "Neznámá verze"; }
    }
    private static string Bound(string value) => value.Length <= 600 ? value : value[..(char.IsHighSurrogate(value[599]) ? 599 : 600)] + "…";
    private static TextBlock Text(string value, double size = 13) => new() { Text = value, FontSize = size, FontFamily = UiTypography.Font,
        Foreground = Display.Brush(0x95a4ad), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static Button Command(string label) => new() { Content = label, FontSize = 12, FontFamily = UiTypography.Font, Height = 32, MinHeight = 0,
        Padding = new Thickness(12, 0, 12, 0), Background = Display.Brush(0x1d252a) };
}
