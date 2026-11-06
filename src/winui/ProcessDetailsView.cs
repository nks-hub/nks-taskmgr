using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;
using Windows.Graphics;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Button processDetailsButton = Button("");
    private readonly ProcessDetailsClient processDetailsClient = new();
    private ProcessDetailsView? processDetailsWindow;

    private void ConfigureProcessDetails()
    {
        processDetailsButton.Width = 35; processDetailsButton.Padding = new Thickness(0); processDetailsButton.IsEnabled = false;
        processDetailsButton.Content = UiIcon.Create("processes", 0x95a4ad, 19);
        AutomationProperties.SetName(processDetailsButton, Loc.T("Podrobnosti vybraného procesu"));
        AutomationProperties.SetAutomationId(processDetailsButton, "process-details");
        ToolTipService.SetToolTip(processDetailsButton, Loc.T("Vlákna, moduly, paměť a handly procesu"));
        processDetailsButton.Click += async (_, _) => {
            if (SelectedProcess is not { } row || !CanReadDetails(row)) return;
            try {
                if (processDetailsWindow is { } previous && previous.Key != row.Key) { processDetailsWindow = null; previous.Close(); }
                if (processDetailsWindow is null) {
                    var window = new ProcessDetailsView(processDetailsClient, row.Key, row.Name, () => model.IsCurrent(row),
                        row.Created100ns, ExecuteActionRequestAsync, () => model.IsCurrent(row) ? row : null,
                        pid => model.Processes.FirstOrDefault(process => process.Key.Pid == pid)?.Name);
                    window.RequestElevation = () => _ = OfferRestartElevatedAsync(Loc.T("Podrobnosti procesů jiných uživatelů a systému vyžadují práva správce."));
                    processDetailsWindow = window;
                    window.Closed += (_, _) => { if (ReferenceEquals(processDetailsWindow, window)) processDetailsWindow = null; };
                }
                processDetailsWindow.ShowOwned(this);
            }
            catch (Exception error) when (error is Win32Exception or COMException or InvalidOperationException) {
                processDetailsWindow?.Close(); processDetailsWindow = null;
                await new ContentDialog { Title = Loc.T("Podrobnosti nelze otevřít"), Content = error.Message,
                    CloseButtonText = Loc.T("Zavřít"), XamlRoot = root.XamlRoot }.ShowAsync();
            }
        };
        Closed += (_, _) => { processDetailsWindow?.Close(); processDetailsClient.Dispose(); };
    }

    private bool CanReadDetails(ProcessRow row) => model.IsCurrent(row) && !row.Key.Boot.IsEmpty && row.Key.Generation != 0
        && row.Key.Kind is GenerationKind.CreationTime or GenerationKind.SequenceNumber;

    private void UpdateProcessDetailsCommand()
    {
        SetEnabled(processDetailsButton, SelectedProcess is { } row && CanReadDetails(row));
        processDetailsWindow?.UpdateAvailability();
    }
}

internal sealed partial class ProcessDetailsView : Window
{
    private static readonly CultureInfo Czech = Loc.Culture;
    private readonly ProcessDetailsClient client;
    private readonly Func<bool> isCurrent;
    private readonly Grid layout = new() { Padding = new Thickness(16), RowSpacing = 10, Background = Display.Brush(0x171d21) };
    private readonly Grid body = new() { ColumnSpacing = 12, RowSpacing = 8 };
    private readonly Button threads = Command(Loc.T("Vlákna")), modules = Command(Loc.T("Moduly")), refresh = Command(Loc.T("Obnovit")), cancel = Command(Loc.T("Zrušit")), verify = Command(Loc.T("Ověřit podpis souboru"));
    private readonly TextBlock title = Label("", 18), state = Label(Loc.T("Podrobnosti se načítají na vyžádání."), 12), count = Label("", 12);
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        FontFamily = UiTypography.Font, FontSize = 12, MinHeight = 0, Text = Loc.T("Vyberte řádek ze seznamu.") };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0) };
    private readonly ProcessDetailsTemplates templates = new();
    private readonly Grid inspector = new() { RowSpacing = 8 };
    private readonly TextBlock signatureNote = Label(Loc.T("Podpis se ověřuje pouze u souboru na disku, místně a z mezipaměti certifikátů."), 11);
    private ProcessDetailsSnapshot? threadSnapshot, moduleSnapshot;
    private ModuleSignatureInfo? signature;
    private DateTimeOffset? signatureTime;
    private CancellationTokenSource? operation;
    private ProcessDetailsCommand mode = ProcessDetailsCommand.Threads;
    private bool activated, closed, targetChanged;
    internal ProcessKey Key { get; }

    internal ProcessDetailsView(ProcessDetailsClient client, ProcessKey key, string name, Func<bool> isCurrent,
        ulong created100ns = 0, Func<ProcessActionsRequest, bool, CancellationToken, Task<ProcessActionsBatch>>? executeAction = null,
        Func<ProcessRow?>? liveRow = null, Func<uint, string?>? nameOf = null)
    {
        this.client = client; Key = key; this.isCurrent = isCurrent;
        this.created100ns = created100ns; this.executeAction = executeAction; processName = Flat(name);
        Title = Loc.T("Podrobnosti procesu") + " · NKS Task Manager";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico")); AppWindow.ResizeDip(1100, 780);
        Appearance.Attach(this, layout);
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        title.Text = Flat(name) + " · PID " + key.Pid.ToString(Czech); title.TextWrapping = TextWrapping.Wrap;
        title.MaxLines = 2; title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.Foreground = Display.Brush(0xe9eef0); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        layout.Children.Add(title);
        var commands = new Grid { ColumnSpacing = 8 };
        commands.ColumnDefinitions.Add(new ColumnDefinition()); commands.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 }; modes.Children.Add(threads); modes.Children.Add(modules);
        ConfigureObjects(modes);
        ConfigureOverview(modes, liveRow, nameOf);
        var control = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 }; control.Children.Add(elevate); control.Children.Add(refresh); control.Children.Add(cancel);
        commands.Children.Add(modes); commands.Children.Add(control); Grid.SetColumn(control, 1); Grid.SetRow(commands, 1); layout.Children.Add(commands);
        state.TextWrapping = TextWrapping.Wrap; Grid.SetRow(state, 2); layout.Children.Add(state);
        body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition());
        body.RowDefinitions.Add(new RowDefinition()); body.RowDefinitions.Add(new RowDefinition());
        var listStyle = new Style { TargetType = typeof(ListViewItem) };
        listStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34d));
        listStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        listStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
        list.ItemContainerStyle = listStyle; ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.ItemTemplate = (DataTemplate)templates["ProcessDetailsRow"];
        var listPane = new Grid();
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); listPane.RowDefinitions.Add(new RowDefinition());
        listPane.Children.Add(listHeader); listPane.Children.Add(list); Grid.SetRow(list, 1);
        body.Children.Add(listPane);
        inspector.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); inspector.RowDefinitions.Add(new RowDefinition()); inspector.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inspector.Children.Add(verify); inspector.Children.Add(detail); Grid.SetRow(detail, 1);
        signatureNote.TextWrapping = TextWrapping.Wrap; inspector.Children.Add(signatureNote); Grid.SetRow(signatureNote, 2); body.Children.Add(inspector);
        Grid.SetRow(body, 4); layout.Children.Add(body); Grid.SetRow(count, 5); layout.Children.Add(count);
        Grid.SetRow(overview, 3); Grid.SetRowSpan(overview, 2); layout.Children.Add(overview);
        Identify(list, "process-details-list", Loc.T("Vlákna nebo moduly vybraného procesu"));
        Identify(detail, "process-details-selection", Loc.T("Údaje vybraného vlákna nebo modulu pouze pro čtení"));
        Identify(state, "process-details-status", Loc.T("Stav načítání podrobností"));
        Identify(refresh, "process-details-refresh", Loc.T("Obnovit podrobnosti stejného procesu"));
        Identify(cancel, "process-details-cancel", Loc.T("Zrušit dotaz a ukončit jeho pracovníka"));
        Identify(threads, "process-details-threads", Loc.T("Vlákna procesu")); Identify(modules, "process-details-modules", Loc.T("Moduly procesu"));
        Identify(verify, "process-details-signature", Loc.T("Ověřit podpis souboru vybraného modulu na disku"));
        threads.Click += async (_, _) => await SelectModeAsync(ProcessDetailsCommand.Threads);
        modules.Click += async (_, _) => await SelectModeAsync(ProcessDetailsCommand.Modules);
        refresh.Click += async (_, _) => await ReadAsync(mode);
        cancel.Click += (_, _) => operation?.Cancel();
        verify.Click += async (_, _) => { if (SelectedModule is { } module) await ReadAsync(ProcessDetailsCommand.Signature, module); };
        list.SelectionChanged += (_, _) => SelectionUpdated();
        layout.SizeChanged += (_, _) => Resize();
        Activated += async (_, _) => { if (!activated) { activated = true; ShowOverview(true); await ReadAsync(ProcessDetailsCommand.Threads); } };
        Closed += (_, _) => { closed = true; operation?.Cancel(); filterOperation?.Cancel(); searchTimer.Stop(); };
        Content = layout; UpdateAvailability(); Resize();
    }

    private static Button Command(string text) => new() { Content = text, FontFamily = UiTypography.Font, FontSize = 12,
        Height = 35, MinHeight = 0, Padding = new Thickness(12, 0, 12, 0) };
    private static TextBlock Label(string value, double size) => new() { Text = value, FontFamily = UiTypography.Font, FontSize = size, Foreground = Display.Brush(0x95a4ad) };
    private static void Identify(DependencyObject value, string id, string name) { AutomationProperties.SetAutomationId(value, id); AutomationProperties.SetName(value, name); }
    private static string Flat(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    private object? SelectedRow => (list.SelectedItem as ProcessDetailsRow)?.Source;
    private ProcessModuleInfo? SelectedModule => mode == ProcessDetailsCommand.Modules ? SelectedRow as ProcessModuleInfo : null;

    internal void ShowOwned(Window owner)
    {
        ObjectDisposedException.ThrowIf(closed, this);
        WindowOwnership.Set(this, owner);
        WindowOwnership.Restore(this);
        Activate();
    }

    private void Resize()
    {
        bool narrow = layout.ActualWidth < 850;
        body.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        body.ColumnDefinitions[0].Width = new GridLength(3, GridUnitType.Star);
        body.RowDefinitions[1].Height = narrow ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        body.RowDefinitions[0].Height = new GridLength(3, GridUnitType.Star);
        Grid.SetColumn(inspector, narrow ? 0 : 1); Grid.SetRow(inspector, narrow ? 1 : 0);
    }

    internal void UpdateAvailability()
    {
        if (closed) return;
        bool current = !targetChanged && isCurrent(), busy = operation is not null;
        threads.IsEnabled = !busy && (current || threadSnapshot is not null);
        modules.IsEnabled = !busy && (current || moduleSnapshot is not null);
        memory.IsEnabled = !busy && (current || memorySnapshot is not null);
        handles.IsEnabled = !busy && (current || handlesSnapshot is not null);
        refresh.IsEnabled = current && !busy;
        cancel.IsEnabled = busy;
        overviewTab.IsEnabled = !busy;
        verify.Visibility = signatureNote.Visibility = mode == ProcessDetailsCommand.Modules ? Visibility.Visible : Visibility.Collapsed;
        verify.IsEnabled = current && !busy && SelectedModule is { Path.Length: > 0 };
        observeObject.IsEnabled = current && !busy && SelectedHandle is not null;
        observeObject.Visibility = mode == ProcessDetailsCommand.Handles ? Visibility.Visible : Visibility.Collapsed;
        if (!current) { operation?.Cancel(); state.Text = Loc.T("Proces skončil nebo jeho identita už neodpovídá. Zobrazené údaje jsou poslední načtený snímek."); }
        threads.Background = Display.Brush(!showingOverview && mode == ProcessDetailsCommand.Threads ? 0x28343bu : 0x1d252au);
        modules.Background = Display.Brush(!showingOverview && mode == ProcessDetailsCommand.Modules ? 0x28343bu : 0x1d252au);
        memory.Background = Display.Brush(!showingOverview && mode == ProcessDetailsCommand.Memory ? 0x28343bu : 0x1d252au);
        handles.Background = Display.Brush(!showingOverview && mode == ProcessDetailsCommand.Handles ? 0x28343bu : 0x1d252au);
    }

    private async Task SelectModeAsync(ProcessDetailsCommand selected)
    {
        if (operation is not null || closed) return;
        ShowOverview(false);
        mode = selected; signature = null; signatureTime = null; ShowRows(); UpdateAvailability();
        if (SnapshotFor(selected) is null) await ReadAsync(selected);
    }

    private async Task ReadAsync(ProcessDetailsCommand command, ProcessModuleInfo? module = null, ProcessHandleInfo? handle = null)
    {
        if (closed || operation is not null) return;
        if (targetChanged || !isCurrent()) { UpdateAvailability(); return; }
        using var current = new CancellationTokenSource(); operation = current; UpdateAvailability();
        state.Text = command switch { ProcessDetailsCommand.Signature => Loc.T("Ověřuji soubor vybraného modulu na disku…"),
            ProcessDetailsCommand.HandleInfo => Loc.T("Zjišťuji aktuální typ a název vybraného objektu…"), _ => Loc.F("Načítám {0}…", ModeName(command)) };
        try {
            var snapshot = await (command switch {
                ProcessDetailsCommand.Memory => client.ReadMemoryAsync(Key, current.Token),
                ProcessDetailsCommand.Handles => client.ReadHandlesAsync(Key, current.Token),
                ProcessDetailsCommand.HandleInfo when handle is not null => client.ReadHandleInfoAsync(Key, handle.Value, current.Token),
                _ => client.ReadAsync(command, Key, module, current.Token)
            });
            if (closed) return;
            current.Token.ThrowIfCancellationRequested();
            if (!isCurrent() || snapshot.State == ProcessDetailsState.TargetChanged) {
                targetChanged = true; UpdateAvailability(); return;
            }
            ShowAccessDenied(snapshot.State == ProcessDetailsState.AccessDenied);
            if (snapshot.State != ProcessDetailsState.Ok) { state.Text = ErrorText(snapshot); return; }
            if (command == ProcessDetailsCommand.Signature) { signature = snapshot.Signature; signatureTime = snapshot.ReceivedAt; UpdateDetail(); }
            else if (command == ProcessDetailsCommand.HandleInfo) {
                if (ReferenceEquals(handle, SelectedHandle)) { observation = snapshot.HandleInfo; observationHandle = handle; observationTime = snapshot.ReceivedAt; UpdateDetail(); }
            }
            else {
                switch (command) {
                    case ProcessDetailsCommand.Threads: threadSnapshot = snapshot; break;
                    case ProcessDetailsCommand.Modules: moduleSnapshot = snapshot; signature = null; signatureTime = null; break;
                    case ProcessDetailsCommand.Memory: memorySnapshot = snapshot; break;
                    case ProcessDetailsCommand.Handles: handlesSnapshot = snapshot; break;
                }
                mode = command; ShowRows();
            }
            state.Text = Architecture(snapshot.Architecture) + " · " + Loc.F("načteno {0}", snapshot.ReceivedAt.LocalDateTime.ToString("HH:mm:ss", Czech))
                + (snapshot.PartialMetrics ? " · " + Loc.T("některé údaje jsou nedostupné") : "");
        } catch (OperationCanceledException) { if (!closed) state.Text = isCurrent() && !targetChanged ? Loc.T("Dotaz byl zrušen; pracovník se ukončuje.") : Loc.T("Proces už není aktuální. Poslední načtené údaje zůstávají pouze pro čtení."); }
        catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception or TimeoutException or ArgumentException or ObjectDisposedException) {
            if (!closed) state.Text = Loc.F("Podrobnosti se nepodařilo načíst: {0}", error.Message);
        } finally { operation = null; if (!closed) UpdateAvailability(); }
    }

    private void ShowRows()
    {
        ClearObservation(); applyingRows = true;
        try { list.ItemsSource = null; } finally { applyingRows = false; }
        ConfigureFilters(); QueueFilter(true); UpdateDetail(); UpdateListHeader();
    }

    private void UpdateDetail()
    {
        if (SelectedRow is null) {
            detail.Text = SnapshotFor(mode) is null ? Loc.T("Podrobnosti zatím nebyly načtené.")
                : list.Items.Count == 0 ? Loc.T("Seznam je prázdný. Zkontrolujte hledání a filtr.") : Loc.T("Vyberte řádek ze seznamu.");
            return;
        }
        if (UpdateObjectDetail()) return;
        var text = new StringBuilder();
        if (SelectedRow is ProcessThreadInfo thread) {
            text.Append("TID ").Append(thread.Id.ToString(Czech)).AppendLine().AppendLine();
            text.Append(Loc.F("Základní priorita: {0}", thread.BasePriority.ToString(Czech))).AppendLine();
            text.Append(Loc.F("Relativní priorita: {0}", thread.PriorityState == SampleState.Valid ? thread.RelativePriority.ToString(Czech) : MetricState(thread.PriorityState, thread.PriorityError))).AppendLine();
            text.Append(Loc.F("Vytvořeno: {0}", thread.TimesState == SampleState.Valid ? FileTime(thread.Created100ns) : MetricState(thread.TimesState, thread.TimesError))).AppendLine();
            text.Append(Loc.F("Čas v jádře: {0}", Time(thread.Kernel100ns, thread.TimesState, thread.TimesError))).AppendLine();
            text.Append(Loc.F("Čas v uživatelském režimu: {0}", Time(thread.User100ns, thread.TimesState, thread.TimesError)));
        } else if (SelectedModule is { } module) {
            text.Append(module.Name.Length == 0 ? Loc.T("Název nedostupný") : module.Name).AppendLine().AppendLine().Append(Loc.F("Základní adresa: 0x{0}", module.BaseAddress.ToString("X16", Czech))).AppendLine();
            text.Append(Loc.F("Velikost obrazu: {0} B", module.ImageSize.ToString("N0", Czech))).AppendLine();
            text.Append(Loc.F("Cesta: {0}", module.Path.Length == 0 ? Loc.T("Nedostupná") : module.Path)).AppendLine().AppendLine();
            if (signature is not null && string.Equals(signature.Path, module.Path, StringComparison.OrdinalIgnoreCase)) {
                text.Append(Loc.F("Podpis souboru na disku: {0}", signature.State switch { ModuleSignatureState.Valid => Loc.T("Platný podle místního ověření Windows"), ModuleSignatureState.Unsigned => Loc.T("Bez podpisu"), ModuleSignatureState.Untrusted => Loc.T("Nedůvěryhodný nebo neplatný"), _ => Loc.T("Nedostupný") })).AppendLine();
                text.Append(Loc.F("Zdroj: {0}", signature.Source switch { ModuleSignatureSource.Embedded => Loc.T("Vložený podpis"), ModuleSignatureSource.Catalog => Loc.T("Katalog"), _ => Loc.T("Nezjištěn") })).AppendLine();
                text.Append(Loc.F("Vydavatel: {0}", signature.Publisher.Length == 0 ? Loc.T("Nedostupný") : signature.Publisher)).AppendLine();
                text.Append(Loc.F("Výsledek Windows: 0x{0}", unchecked((uint)signature.TrustCode).ToString("X8", Czech))).AppendLine();
                text.Append(Loc.F("Načteno: {0}", signatureTime?.LocalDateTime.ToString("g", Czech)));
            } else text.Append(Loc.T("Podpis souboru zatím není ověřený."));
        }
        detail.Text = text.ToString();
    }

    private static string Time(ulong ticks, SampleState state, uint error) => state == SampleState.Valid ? (ticks / 10_000_000m).ToString("N3", Czech) + " s" : MetricState(state, error);
    private static string FileTime(ulong value)
    {
        if (value <= long.MaxValue) {
            try { return DateTime.FromFileTimeUtc((long)value).ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", Czech); }
            catch (ArgumentOutOfRangeException) { }
        }
        return Loc.F("{0} × 100 ns od 1601-01-01", value.ToString(Czech));
    }
    private static string MetricState(SampleState state, uint error) => (state switch {
        SampleState.First => Loc.T("První vzorek"), SampleState.AccessDenied => Loc.T("Chybí oprávnění"), SampleState.Unsupported => Loc.T("Nepodporováno"),
        SampleState.Stale => Loc.T("Zastaralé"), SampleState.Gap => Loc.T("Mezera"), SampleState.Error => Loc.T("Chyba dotazu"), _ => Loc.T("Nedostupné")
    }) + (error == 0 ? "" : " (Windows " + error.ToString(Czech) + ")");
    private static string Architecture(ProcessArchitecture value) => value switch { ProcessArchitecture.X86 => Loc.T("32bitový proces (WOW64)"), ProcessArchitecture.X64 => Loc.T("64bitový proces"), ProcessArchitecture.Arm64 => "ARM64", _ => Loc.T("Bitovost nezjištěna") };
    private static string ErrorText(ProcessDetailsSnapshot snapshot) => (snapshot.Architecture == ProcessArchitecture.Unknown ? "" : Architecture(snapshot.Architecture) + " · ") + (snapshot.State switch {
        ProcessDetailsState.AccessDenied => Loc.T("Chybí oprávnění k těmto podrobnostem."), ProcessDetailsState.Unsupported => Loc.T("Tento zdroj nebo bitovost nejsou podporované."),
        ProcessDetailsState.Unavailable => Loc.T("Požadované podrobnosti nejsou dostupné."), ProcessDetailsState.InvalidRequest => Loc.T("Pracovník odmítl požadavek."),
        ProcessDetailsState.LimitExceeded => Loc.T("Dotaz překročil datový limit; neúplná data se nezobrazují."), _ => Loc.T("Pracovník ohlásil chybu dotazu.")
    }) + (snapshot.Error == 0 ? "" : " Windows " + snapshot.Error.ToString(Czech) + ".")
        + (snapshot.NtStatus == 0 ? "" : " NT 0x" + unchecked((uint)snapshot.NtStatus).ToString("X8", Czech) + ".");
}
