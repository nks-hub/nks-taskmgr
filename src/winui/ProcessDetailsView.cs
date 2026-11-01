using System.ComponentModel;
using System.Globalization;
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
        AutomationProperties.SetName(processDetailsButton, "Podrobnosti vybraného procesu");
        AutomationProperties.SetAutomationId(processDetailsButton, "process-details");
        ToolTipService.SetToolTip(processDetailsButton, "Vlákna, moduly a podpis souboru");
        processDetailsButton.Click += (_, _) => {
            if (SelectedProcess is not { } row || !CanReadDetails(row)) return;
            if (processDetailsWindow is { } previous && previous.Key != row.Key) { processDetailsWindow = null; previous.Close(); }
            if (processDetailsWindow is null) {
                var window = new ProcessDetailsView(processDetailsClient, row.Key, row.Name, () => model.IsCurrent(row));
                processDetailsWindow = window;
                window.Closed += (_, _) => { if (ReferenceEquals(processDetailsWindow, window)) processDetailsWindow = null; };
            }
            processDetailsWindow.Activate();
        };
        Closed += (_, _) => { processDetailsWindow?.Close(); processDetailsClient.Dispose(); };
    }

    private bool CanReadDetails(ProcessRow row) => model.IsCurrent(row) && !row.Key.Boot.IsEmpty && row.Key.Generation != 0
        && row.Key.Kind is GenerationKind.CreationTime or GenerationKind.SequenceNumber;

    private void UpdateProcessDetailsCommand()
    {
        processDetailsButton.IsEnabled = SelectedProcess is { } row && CanReadDetails(row);
        processDetailsWindow?.UpdateAvailability();
    }
}

internal sealed partial class ProcessDetailsView : Window
{
    private static readonly CultureInfo Czech = CultureInfo.GetCultureInfo("cs-CZ");
    private readonly ProcessDetailsClient client;
    private readonly Func<bool> isCurrent;
    private readonly Grid layout = new() { Padding = new Thickness(16), RowSpacing = 10, Background = Display.Brush(0x171d21) };
    private readonly Grid body = new() { ColumnSpacing = 12, RowSpacing = 8 };
    private readonly Button threads = Command("Vlákna"), modules = Command("Moduly"), refresh = Command("Obnovit"), cancel = Command("Zrušit"), verify = Command("Ověřit podpis souboru");
    private readonly TextBlock title = Label("", 18), state = Label("Podrobnosti se načítají na vyžádání.", 12), count = Label("", 12);
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas"), FontSize = 12, MinHeight = 0, Text = "Vyberte řádek ze seznamu." };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0) };
    private readonly Grid inspector = new() { RowSpacing = 8 };
    private readonly TextBlock signatureNote = Label("Podpis se ověřuje pouze u souboru na disku, místně a z mezipaměti certifikátů.", 11);
    private ProcessDetailsSnapshot? threadSnapshot, moduleSnapshot;
    private ModuleSignatureInfo? signature;
    private DateTimeOffset? signatureTime;
    private CancellationTokenSource? operation;
    private ProcessDetailsCommand mode = ProcessDetailsCommand.Threads;
    private bool activated, closed, targetChanged;
    internal ProcessKey Key { get; }

    internal ProcessDetailsView(ProcessDetailsClient client, ProcessKey key, string name, Func<bool> isCurrent)
    {
        this.client = client; Key = key; this.isCurrent = isCurrent;
        Title = "Podrobnosti procesu · NKS Task Manager";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico")); AppWindow.Resize(new SizeInt32(1100, 780));
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
        var control = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 }; control.Children.Add(refresh); control.Children.Add(cancel);
        commands.Children.Add(modes); commands.Children.Add(control); Grid.SetColumn(control, 1); Grid.SetRow(commands, 1); layout.Children.Add(commands);
        state.TextWrapping = TextWrapping.Wrap; Grid.SetRow(state, 2); layout.Children.Add(state);
        body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition());
        body.RowDefinitions.Add(new RowDefinition()); body.RowDefinitions.Add(new RowDefinition());
        var listStyle = new Style { TargetType = typeof(ListViewItem) };
        listStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34d));
        listStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
        list.ItemContainerStyle = listStyle; ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        body.Children.Add(list);
        inspector.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); inspector.RowDefinitions.Add(new RowDefinition()); inspector.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inspector.Children.Add(verify); inspector.Children.Add(detail); Grid.SetRow(detail, 1);
        signatureNote.TextWrapping = TextWrapping.Wrap; inspector.Children.Add(signatureNote); Grid.SetRow(signatureNote, 2); body.Children.Add(inspector);
        Grid.SetRow(body, 4); layout.Children.Add(body); Grid.SetRow(count, 5); layout.Children.Add(count);
        Identify(list, "process-details-list", "Vlákna nebo moduly vybraného procesu");
        Identify(detail, "process-details-selection", "Údaje vybraného vlákna nebo modulu pouze pro čtení");
        Identify(state, "process-details-status", "Stav načítání podrobností");
        Identify(refresh, "process-details-refresh", "Obnovit podrobnosti stejného procesu");
        Identify(cancel, "process-details-cancel", "Zrušit dotaz a ukončit jeho pracovníka");
        Identify(threads, "process-details-threads", "Vlákna procesu"); Identify(modules, "process-details-modules", "Moduly procesu");
        Identify(verify, "process-details-signature", "Ověřit podpis souboru vybraného modulu na disku");
        threads.Click += async (_, _) => await SelectModeAsync(ProcessDetailsCommand.Threads);
        modules.Click += async (_, _) => await SelectModeAsync(ProcessDetailsCommand.Modules);
        refresh.Click += async (_, _) => await ReadAsync(mode);
        cancel.Click += (_, _) => operation?.Cancel();
        verify.Click += async (_, _) => { if (SelectedModule is { } module) await ReadAsync(ProcessDetailsCommand.Signature, module); };
        list.SelectionChanged += (_, _) => SelectionUpdated();
        layout.SizeChanged += (_, _) => Resize();
        Activated += async (_, _) => { if (!activated) { activated = true; await ReadAsync(ProcessDetailsCommand.Threads); } };
        Closed += (_, _) => { closed = true; operation?.Cancel(); filterOperation?.Cancel(); searchTimer.Stop(); };
        Content = layout; UpdateAvailability(); Resize();
    }

    private static Button Command(string text) => new() { Content = text, FontFamily = UiTypography.Font, FontSize = 12,
        Height = 35, MinHeight = 0, Padding = new Thickness(12, 0, 12, 0) };
    private static TextBlock Label(string value, double size) => new() { Text = value, FontFamily = UiTypography.Font, FontSize = size, Foreground = Display.Brush(0x95a4ad) };
    private static void Identify(DependencyObject value, string id, string name) { AutomationProperties.SetAutomationId(value, id); AutomationProperties.SetName(value, name); }
    private static string Flat(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    private ProcessModuleInfo? SelectedModule => mode == ProcessDetailsCommand.Modules ? list.SelectedItem as ProcessModuleInfo : null;

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
        verify.Visibility = signatureNote.Visibility = mode == ProcessDetailsCommand.Modules ? Visibility.Visible : Visibility.Collapsed;
        verify.IsEnabled = current && !busy && SelectedModule is { Path.Length: > 0 };
        observeObject.IsEnabled = current && !busy && SelectedHandle is not null;
        observeObject.Visibility = mode == ProcessDetailsCommand.Handles ? Visibility.Visible : Visibility.Collapsed;
        if (!current) { operation?.Cancel(); state.Text = "Proces skončil nebo jeho identita už neodpovídá. Zobrazené údaje jsou poslední načtený snímek."; }
        threads.Background = Display.Brush(mode == ProcessDetailsCommand.Threads ? 0x28343bu : 0x1d252au);
        modules.Background = Display.Brush(mode == ProcessDetailsCommand.Modules ? 0x28343bu : 0x1d252au);
        memory.Background = Display.Brush(mode == ProcessDetailsCommand.Memory ? 0x28343bu : 0x1d252au);
        handles.Background = Display.Brush(mode == ProcessDetailsCommand.Handles ? 0x28343bu : 0x1d252au);
    }

    private async Task SelectModeAsync(ProcessDetailsCommand selected)
    {
        if (operation is not null || closed) return;
        mode = selected; signature = null; signatureTime = null; ShowRows(); UpdateAvailability();
        if (SnapshotFor(selected) is null) await ReadAsync(selected);
    }

    private async Task ReadAsync(ProcessDetailsCommand command, ProcessModuleInfo? module = null, ProcessHandleInfo? handle = null)
    {
        if (closed || operation is not null) return;
        if (targetChanged || !isCurrent()) { UpdateAvailability(); return; }
        using var current = new CancellationTokenSource(); operation = current; UpdateAvailability();
        state.Text = command switch { ProcessDetailsCommand.Signature => "Ověřuji soubor vybraného modulu na disku…",
            ProcessDetailsCommand.HandleInfo => "Zjišťuji aktuální typ a název vybraného objektu…", _ => "Načítám " + ModeName(command) + "…" };
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
            state.Text = Architecture(snapshot.Architecture) + " · načteno " + snapshot.ReceivedAt.LocalDateTime.ToString("HH:mm:ss", Czech)
                + (snapshot.PartialMetrics ? " · některé údaje jsou nedostupné" : "");
        } catch (OperationCanceledException) { if (!closed) state.Text = isCurrent() && !targetChanged ? "Dotaz byl zrušen; pracovník se ukončuje." : "Proces už není aktuální. Poslední načtené údaje zůstávají pouze pro čtení."; }
        catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception or TimeoutException or ArgumentException or ObjectDisposedException) {
            if (!closed) state.Text = "Podrobnosti se nepodařilo načíst: " + error.Message;
        } finally { operation = null; if (!closed) UpdateAvailability(); }
    }

    private void ShowRows()
    {
        ClearObservation(); applyingRows = true;
        try { list.ItemsSource = null; } finally { applyingRows = false; }
        ConfigureFilters(); QueueFilter(true); UpdateDetail();
    }

    private void UpdateDetail()
    {
        if (UpdateObjectDetail()) return;
        if (list.SelectedItem is null) { detail.Text = "Vyberte řádek ze seznamu."; return; }
        var text = new StringBuilder();
        if (list.SelectedItem is ProcessThreadInfo thread) {
            text.Append("TID ").Append(thread.Id.ToString(Czech)).AppendLine().AppendLine();
            text.Append("Základní priorita: ").Append(thread.BasePriority.ToString(Czech)).AppendLine();
            text.Append("Relativní priorita: ").Append(thread.PriorityState == SampleState.Valid ? thread.RelativePriority.ToString(Czech) : MetricState(thread.PriorityState, thread.PriorityError)).AppendLine();
            text.Append("Vytvořeno: ").Append(thread.TimesState == SampleState.Valid ? FileTime(thread.Created100ns) : MetricState(thread.TimesState, thread.TimesError)).AppendLine();
            text.Append("Čas v jádře: ").Append(Time(thread.Kernel100ns, thread.TimesState, thread.TimesError)).AppendLine();
            text.Append("Čas v uživatelském režimu: ").Append(Time(thread.User100ns, thread.TimesState, thread.TimesError));
        } else if (SelectedModule is { } module) {
            text.Append(module.Name.Length == 0 ? "Název nedostupný" : module.Name).AppendLine().AppendLine().Append("Základní adresa: 0x").Append(module.BaseAddress.ToString("X16", Czech)).AppendLine();
            text.Append("Velikost obrazu: ").Append(module.ImageSize.ToString("N0", Czech)).Append(" B").AppendLine();
            text.Append("Cesta: ").Append(module.Path.Length == 0 ? "Nedostupná" : module.Path).AppendLine().AppendLine();
            if (signature is not null && string.Equals(signature.Path, module.Path, StringComparison.OrdinalIgnoreCase)) {
                text.Append("Podpis souboru na disku: ").Append(signature.State switch { ModuleSignatureState.Valid => "Platný podle místního ověření Windows", ModuleSignatureState.Unsigned => "Bez podpisu", ModuleSignatureState.Untrusted => "Nedůvěryhodný nebo neplatný", _ => "Nedostupný" }).AppendLine();
                text.Append("Zdroj: ").Append(signature.Source switch { ModuleSignatureSource.Embedded => "Vložený podpis", ModuleSignatureSource.Catalog => "Katalog", _ => "Nezjištěn" }).AppendLine();
                text.Append("Vydavatel: ").Append(signature.Publisher.Length == 0 ? "Nedostupný" : signature.Publisher).AppendLine();
                text.Append("Výsledek Windows: 0x").Append(unchecked((uint)signature.TrustCode).ToString("X8", Czech)).AppendLine();
                text.Append("Načteno: ").Append(signatureTime?.LocalDateTime.ToString("g", Czech));
            } else text.Append("Podpis souboru zatím není ověřený.");
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
        return value.ToString(Czech) + " × 100 ns od 1601-01-01";
    }
    private static string MetricState(SampleState state, uint error) => (state switch {
        SampleState.First => "První vzorek", SampleState.AccessDenied => "Chybí oprávnění", SampleState.Unsupported => "Nepodporováno",
        SampleState.Stale => "Zastaralé", SampleState.Gap => "Mezera", SampleState.Error => "Chyba dotazu", _ => "Nedostupné"
    }) + (error == 0 ? "" : " (Windows " + error.ToString(Czech) + ")");
    private static string Architecture(ProcessArchitecture value) => value switch { ProcessArchitecture.X86 => "32bitový proces (WOW64)", ProcessArchitecture.X64 => "64bitový proces", ProcessArchitecture.Arm64 => "ARM64", _ => "Bitovost nezjištěna" };
    private static string ErrorText(ProcessDetailsSnapshot snapshot) => (snapshot.Architecture == ProcessArchitecture.Unknown ? "" : Architecture(snapshot.Architecture) + " · ") + (snapshot.State switch {
        ProcessDetailsState.AccessDenied => "Chybí oprávnění k těmto podrobnostem.", ProcessDetailsState.Unsupported => "Tento zdroj nebo bitovost nejsou podporované.",
        ProcessDetailsState.Unavailable => "Požadované podrobnosti nejsou dostupné.", ProcessDetailsState.InvalidRequest => "Pracovník odmítl požadavek.",
        ProcessDetailsState.LimitExceeded => "Dotaz překročil datový limit; neúplná data se nezobrazují.", _ => "Pracovník ohlásil chybu dotazu."
    }) + (snapshot.Error == 0 ? "" : " Windows " + snapshot.Error.ToString(Czech) + ".")
        + (snapshot.NtStatus == 0 ? "" : " NT 0x" + unchecked((uint)snapshot.NtStatus).ToString("X8", Czech) + ".");
}
