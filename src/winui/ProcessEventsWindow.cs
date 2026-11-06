using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;
using Windows.Graphics;
using System.Diagnostics;

namespace NksTaskMgr.WinUI;

internal sealed partial class ProcessEventsWindow : Window
{
    private const int MaximumRows = 10000;
    private const int MaximumEstimatedBytes = 3 * 1024 * 1024;
    private readonly Grid layout = new() { Padding = new Thickness(16), RowSpacing = 8, Background = Display.Brush(0x171d21) };
    private readonly Button start = Command(Loc.T("Spustit nový záznam")), stop = Command(Loc.T("Zastavit")), clear = Command(Loc.T("Vymazat seznam")), elevate = Command(Loc.T("Spustit jako správce"));
    /// <summary>Set by the main window: restarts the application elevated so ETW capture can start.</summary>
    internal Action? RequestElevation { get; set; }
    private readonly TextBlock status = Text(Loc.T("Záznam není spuštěný.")), counters = Text(""), notice = Text(Loc.T("ETW sleduje vznik a ukončení procesů až od spuštění záznamu. Historické řádky jsou pouze pro čtení."));
    private readonly TextBox search = new() { PlaceholderText = Loc.T("Filtrovat název nebo PID"), MinWidth = 180, MinHeight = 32 };
    private readonly ComboBox kinds = new() { Width = 175, MinHeight = 32 };
    private readonly ComboBox modes = new() { Width = 205, MinHeight = 32 };
    private readonly CheckBox hold = new() { Content = Loc.T("Podržet seznam") }, follow = new() { Content = Loc.T("Sledovat konec"), IsChecked = true };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0) };
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12, Text = Loc.T("Vyberte událost.") };
    private readonly List<ProcessEventRow> retained = [];
    private readonly ProcessEventRows visible = [];
    private readonly ProcessTrafficRows trafficRows = new();
    private readonly ProcessLifetimeStore lifetimes = new();
    private readonly ProcessEventTemplates templates = new();
    private readonly Dictionary<int, (ulong Epoch, ulong Id)> selections = new();
    private readonly int[] kindFilters = new int[3];
    private readonly string[] queries = ["", "", ""];
    private readonly DispatcherQueueTimer timer, filterTimer;
    private readonly Func<(long Time, BootIdentifier Boot, SampleState State)> currentProcesses;
    private NativeEventMonitor? monitor;
    private ProcessEventMonitorState state = ProcessEventMonitorState.Idle;
    private uint batchFlags;
    private ulong trimmed, resyncVersion;
    private ulong captureEpoch, heldTrimmed;
    private int estimatedBytes;
    private long originQpc, resyncAfter;
    private bool closed, closing, mainClosing, allowClose, dirty;
    private bool hasOrigin, changingMode, refreshing;
    private int shownMode;
    private bool trafficSupported = true;
    private Task<bool>? closeStop;
    private ProcessTrafficBatchInfo? trafficInfo;
    private bool TrafficMode => modes.SelectedIndex == 1;
    private bool LifetimeMode => modes.SelectedIndex == 2;

    internal ProcessEventsWindow(Func<(long Time, BootIdentifier Boot, SampleState State)> currentProcesses)
    {
        this.currentProcesses = currentProcesses;
        Title = Loc.T("Události procesů") + " · NKS Task Manager";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.ResizeDip(1120, 780); Appearance.Attach(this, layout);
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(155) })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        commands.Children.Add(start); commands.Children.Add(stop); commands.Children.Add(clear); commands.Children.Add(hold); commands.Children.Add(follow);
        layout.Children.Add(new ScrollViewer { Content = commands, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        // One status line; the counters and the explanation stay one click away instead of pushing the list down.
        var information = new StackPanel { Spacing = 8, MaxWidth = 520 };
        foreach (var text in new[] { counters, notice }) { text.TextWrapping = TextWrapping.Wrap; text.IsTextSelectionEnabled = true; information.Children.Add(text); }
        var details = new Button { Content = "ⓘ", Width = 32, Height = 28, MinHeight = 0, Padding = new Thickness(0), FontSize = 14, Background = Display.Brush(0x1d252a),
            Flyout = new Flyout { Content = new ScrollViewer { Content = information, MaxHeight = 360, Padding = new Thickness(0, 0, 16, 0) } } };
        AutomationProperties.SetName(details, Loc.T("Čítače a vysvětlení záznamu")); AutomationProperties.SetAutomationId(details, "events-info");
        ToolTipService.SetToolTip(details, Loc.T("Čítače záznamu, vytěsnění, rozpočet paměti a vysvětlení."));
        status.TextTrimming = TextTrimming.CharacterEllipsis; status.VerticalAlignment = VerticalAlignment.Center; status.IsTextSelectionEnabled = true;
        var messages = new Grid { ColumnSpacing = 6 };
        messages.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); messages.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        messages.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        elevate.Height = 28; elevate.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(elevate, Loc.T("Záznam ETW vyžaduje správce. NKS se spustí znovu jako správce po potvrzení výzvy UAC."));
        AutomationProperties.SetAutomationId(elevate, "events-elevate");
        elevate.Click += (_, _) => RequestElevation?.Invoke();
        messages.Children.Add(status); messages.Children.Add(details); Grid.SetColumn(details, 1);
        messages.Children.Add(elevate); Grid.SetColumn(elevate, 2);
        Grid.SetRow(messages, 1); layout.Children.Add(messages);
        var filters = new Grid { ColumnSpacing = 8 };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters.ColumnDefinitions.Add(new ColumnDefinition()); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var name in new[] { Loc.T("Vznik a ukončení"), Loc.T("Diskové a síťové přenosy"), Loc.T("Životnosti procesů") }) modes.Items.Add(name);
        modes.SelectedIndex = 0;
        filters.Children.Add(modes); filters.Children.Add(search); filters.Children.Add(kinds);
        Grid.SetColumn(search, 1); Grid.SetColumn(kinds, 2); Grid.SetRow(filters, 2); layout.Children.Add(filters);
        foreach (var name in new[] { Loc.T("Všechny události"), Loc.T("Vznik procesů"), Loc.T("Ukončení procesů") }) kinds.Items.Add(name);
        kinds.SelectedIndex = 0;
        list.ItemTemplate = (DataTemplate)templates["ProcessEvent"]; list.ItemsSource = visible;
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        list.ItemContainerStyle = style; Grid.SetRow(list, 3); layout.Children.Add(list);
        ScrollViewer.SetVerticalScrollBarVisibility(detail, ScrollBarVisibility.Auto); Grid.SetRow(detail, 4); layout.Children.Add(detail);
        Content = layout;
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(250); timer.Tick += (_, _) => ReadBatch();
        filterTimer = DispatcherQueue.CreateTimer(); filterTimer.Interval = TimeSpan.FromMilliseconds(150); filterTimer.IsRepeating = false; filterTimer.Tick += (_, _) => RefreshRows();
        start.Click += (_, _) => StartCapture(); stop.Click += (_, _) => StopCapture(); clear.Click += (_, _) => ClearRows();
        search.TextChanged += (_, _) => { if (!changingMode) { filterTimer.Stop(); filterTimer.Start(); } };
        kinds.SelectionChanged += (_, _) => { if (!changingMode) RefreshRows(); }; hold.Unchecked += (_, _) => RefreshRows();
        modes.SelectionChanged += (_, _) => ChangeMode();
        list.SelectionChanged += (_, _) => {
            if (!refreshing && !changingMode && list.SelectedItem is ProcessEventRow row) {
                selections[row.Mode] = row.SelectionIdentity; detail.Text = row.Detail;
            }
        };
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => { closed = true; timer.Stop(); filterTimer.Stop(); monitor?.Dispose(); monitor = null; ProcessNetwork.Clear(); };
        Identify(start, "events-start", Loc.T("Spustit vlastní záznam ETW")); Identify(stop, "events-stop", Loc.T("Zastavit vlastní záznam ETW"));
        Identify(clear, "events-clear", Loc.T("Vymazat zobrazené události")); Identify(search, "events-filter", Loc.T("Filtrovat zaznamenané události"));
        Identify(kinds, "events-kind", Loc.T("Druh události")); Identify(list, "events-list", Loc.T("Historické události, pouze pro čtení"));
        Identify(modes, "events-mode", Loc.T("Životnosti procesů nebo pozorované přenosy ETW"));
        Identify(detail, "events-detail", Loc.T("Podrobnosti historické události")); UpdateCommands();
    }

    private void StartCapture()
    {
        try {
            monitor ??= NativeEventMonitor.Open();
            // Drain the bounded old lifecycle queue and traffic updates before a new capture.
            for (int i = 0; i < 16; ++i) { using var previous = monitor.Read(); }
            state = ProcessEventMonitorState.Starting; monitor.Start();
            ClearRows(); lifetimes.BeginCapture(++captureEpoch); resyncVersion = 0; resyncAfter = 0; originQpc = 0; hasOrigin = false;
            timer.Start(); ReadBatch();
        } catch (Exception error) when (InteropError(error)) { status.Text = Loc.F("Záznam nelze spustit: {0}", error.Message); }
    }

    private void StopCapture()
    {
        try { monitor?.Stop(); ReadBatch(); }
        catch (Exception error) when (InteropError(error)) { status.Text = Loc.F("Zastavení nebylo potvrzené: {0}", error.Message); }
    }

    private int ReadBatch()
    {
        if (closed || monitor is null) return 0;
        try {
            using var frame = monitor.Read();
            var info = frame.Info;
            CapabilityReports.ObserveEvents(info);
            state = info.State; batchFlags = (uint)info.Flags;
            var events = frame.CopyEvents();
            if (events.Length > 0) {
                if (!hasOrigin) { originQpc = events[0].TimestampQpc; hasOrigin = true; }
                foreach (var item in events) {
                    var row = new ProcessEventRow(item, info.QpcFrequency, originQpc, info.Boot, info.BootState);
                    retained.Add(row); estimatedBytes += row.EstimatedBytes;
                }
                int remove = 0;
                while (remove < retained.Count && (retained.Count - remove > MaximumRows || estimatedBytes > MaximumEstimatedBytes))
                    estimatedBytes -= retained[remove++].EstimatedBytes;
                if (remove > 0) { retained.RemoveRange(0, remove); trimmed += (ulong)remove; }
                dirty = true;
            }
            dirty |= lifetimes.ObserveBatch(info, events, originQpc);
            int trafficCount = ReadTraffic(frame, info);
            PruneHeldRows();
            status.Text = StateText(state) + (info.Win32Error != 0 ? " · " + Loc.F("chyba Windows {0}", info.Win32Error) : "");
            var elevation = state == ProcessEventMonitorState.AccessDenied && !MainWindow.RunningElevated && RequestElevation is not null ? Visibility.Visible : Visibility.Collapsed;
            if (elevate.Visibility != elevation) elevate.Visibility = elevation;
            counters.Text = Loc.F("Události {0}/{1} · vytěsněno v okně {2} · ztracené události ETW {3} · ztracené buffery {4} · přeplnění fronty {5} · chyby dekódování {6} · nejasné záznamy {7}",
                retained.Count, MaximumRows, trimmed, info.ProviderEventsLost, info.ProviderBuffersLost, info.QueueDropped, info.DecodeErrors, info.Ambiguous);
            counters.Text += TrafficCounters();
            counters.Text += "\n" + Loc.F("Životnosti {0}/{1} · vytěsněno {2} · neověřené události {3} · konflikty {4} · opožděné pořadí {5} · vynechané pořadí zdroje {6} · označení nejistoty {7}",
                lifetimes.Count, ProcessLifetimeStore.MaximumRows, lifetimes.Trimmed, lifetimes.Unverified, lifetimes.Conflicts, lifetimes.Late, lifetimes.MissingSequences, lifetimes.GapBoundaries);
            if (hold.IsChecked == true) counters.Text += "\n" + Loc.F("Zobrazení je podržené; vytěsněno z podrženého seznamu {0}.", heldTrimmed);
            notice.Text = TrafficMode
                ? Loc.T("Pouze pozorované částečné bajty vlastní relace ETW, nikoli celkový provoz adaptéru ani obecné procesové I/O. TCP/UDP se přiřadí generaci jen při nepřerušené evidenci životností; během záznamu je používají i sloupec Síť v Procesech a Uživatelích.")
                : LifetimeMode ? Loc.T("Start a konec se párují jen přes ověřený Boot + PID + CreateTime v témže záznamu a části zdroje. Nezachycený konec neznamená, že proces stále běží. CPU a RAM se zde neměří.")
                : Loc.T("CreateTime přímo z události s ověřeným bootem určuje historickou životnost. Ostatní řádky zůstávají neověřené.");
            notice.Text += " " + Loc.T("Žádné živé akce podle PID. Čas je relativní QPC; nové spuštění nahradí seznam. Nový základní seznam nezpřesní dřívější nejasné přenosy.");
            if (!trafficSupported) notice.Text += " " + Loc.T("Přenosy tato verze nativní DLL neposkytuje; záznam vzniku a ukončení pokračuje.");
            if ((batchFlags & 1) != 0) {
                if (resyncVersion != info.ResyncVersion) { resyncVersion = info.ResyncVersion; resyncAfter = NativeMonitor.MonotonicNowNs; }
                var current = currentProcesses();
                if (info.BootState == SampleState.Valid && current.State == SampleState.Valid && current.Boot == info.Boot && current.Time > resyncAfter)
                    monitor.AcknowledgeResync(info.ResyncVersion);
                else notice.Text += " " + Loc.T("Základní seznam čeká na nové ověření po změně nebo ztrátě zdroje.");
            }
            if ((batchFlags & 2) != 0) notice.Text += " " + Loc.T("Relace ještě není uklizená.");
            if (dirty && hold.IsChecked != true) RefreshRows();
            UpdateCommands();
            uiCheckBatch?.Invoke(info, events.Length, trafficCount);
            return events.Length + trafficCount;
        } catch (Exception error) when (InteropError(error)) {
            timer.Stop(); ProcessNetwork.Clear(); status.Text = Loc.F("Čtení událostí selhalo: {0}", error.Message); return -1;
        }
    }

    private void ClearRows()
    {
        retained.Clear(); trafficRows.Clear(); lifetimes.Clear(); trafficInfo = null; selections.Clear();
        refreshing = true;
        try { visible.Apply([]); list.SelectedItem = null; }
        finally { refreshing = false; }
        estimatedBytes = 0; trimmed = heldTrimmed = 0; dirty = false; detail.Text = Loc.T("Vyberte událost, životnost nebo pozorovaný přenos.");
    }

    private IEnumerable<ProcessEventRow> ModeRows => LifetimeMode ? lifetimes.Values : TrafficMode ? trafficRows.Values : retained;

    private void RefreshRows(bool force = false)
    {
        if (closed || !force && hold.IsChecked == true) { dirty = true; return; }
        int mode = modes.SelectedIndex;
        if (list.SelectedItem is ProcessEventRow selected && selected.Mode == mode) selections[mode] = selected.SelectionIdentity;
        bool hadSelection = selections.TryGetValue(mode, out var selectedKey);
        string query = search.Text.Trim(); int kind = kinds.SelectedIndex;
        var values = ModeRows.Where(row => (LifetimeMode ? row.LifetimeMatches(kind) : kind == 0 || (TrafficMode ? (int)row.Traffic!.Value.Source == kind : (int)row.Source.Kind == kind))
            && (query.Length == 0 || row.Matches(query))).ToArray();
        var replacement = hadSelection ? values.FirstOrDefault(row => row.SelectionIdentity == selectedKey) : null;
        refreshing = true;
        try {
            visible.Apply(values); list.SelectedItem = replacement; dirty = false;
            if (replacement is not null) detail.Text = replacement.Detail;
            else if (hadSelection) detail.Text = Loc.T("Vybraný záznam už není v aktuálním seznamu nebo filtru.");
            else detail.Text = Loc.T("Vyberte událost, životnost nebo pozorovaný přenos.");
        } finally { refreshing = false; }
        if (follow.IsChecked == true && !hadSelection && values.Length > 0) list.ScrollIntoView(values[^1]);
    }

    private void ChangeMode()
    {
        if (changingMode) return;
        changingMode = true;
        try {
            if (list.SelectedItem is ProcessEventRow selected) selections[selected.Mode] = selected.SelectionIdentity;
            queries[shownMode] = search.Text; kindFilters[shownMode] = Math.Max(0, kinds.SelectedIndex); shownMode = modes.SelectedIndex;
            kinds.Items.Clear();
            foreach (var name in LifetimeMode ? new[] { Loc.T("Všechny životnosti"), Loc.T("Vznik i konec"), Loc.T("Bez konce"), Loc.T("Bez vzniku"), Loc.T("Neověřené"), Loc.T("Konfliktní"), Loc.T("Opožděné"), Loc.T("S mezerou") }
                : TrafficMode ? new[] { Loc.T("Všechny přenosy"), Loc.T("Fyzický disk"), "TCP", "UDP" } : new[] { Loc.T("Všechny události"), Loc.T("Vznik procesů"), Loc.T("Ukončení procesů") }) kinds.Items.Add(name);
            kinds.SelectedIndex = kindFilters[shownMode]; search.Text = queries[shownMode];
            search.PlaceholderText = TrafficMode ? Loc.T("Filtrovat PID, disk, TCP, UDP nebo nejistotu") : Loc.T("Filtrovat název, PID nebo stav");
            list.ItemTemplate = (DataTemplate)templates[LifetimeMode ? "ProcessLifetime" : "ProcessEvent"];
        } finally { changingMode = false; }
        RefreshRows(true);
        if (monitor is not null) ReadBatch();
    }

    private void PruneHeldRows()
    {
        if (hold.IsChecked != true || visible.Count == 0) return;
        var available = ModeRows.Select(row => row.SelectionIdentity).ToHashSet();
        var remaining = visible.Where(row => available.Contains(row.SelectionIdentity)).ToArray();
        if (remaining.Length == visible.Count) return;
        heldTrimmed += (ulong)(visible.Count - remaining.Length);
        var selected = list.SelectedItem as ProcessEventRow;
        refreshing = true;
        try {
            visible.Apply(remaining);
            list.SelectedItem = selected is not null && remaining.Contains(selected) ? selected : null;
            if (selected is not null && list.SelectedItem is null) { selections.Remove(modes.SelectedIndex); detail.Text = Loc.T("Vybraný podržený záznam byl vytěsněn rozpočtem uchování."); }
        } finally { refreshing = false; }
    }

    private int ReadTraffic(NativeEventFrame frame, ProcessEventBatchInfo info)
    {
        if (!trafficSupported) { ProcessNetwork.Clear(); return 0; }
        ProcessTrafficBatch batch;
        try { batch = frame.CopyTraffic(); }
        catch (EntryPointNotFoundException) { trafficSupported = false; trafficRows.Clear(); trafficInfo = null; dirty = true; ProcessNetwork.Clear(); return 0; }
        catch { ProcessNetwork.Clear(); throw; }
        ProcessNetwork.Observe(info, batch);
        trafficInfo = batch.Info;
        if (!hasOrigin && batch.Samples.Length > 0) { originQpc = batch.Samples[0].StartQpc; hasOrigin = true; }
        foreach (var item in batch.Samples) trafficRows.Upsert(item, info.QpcFrequency, originQpc, info.Boot);
        if (batch.Samples.Length > 0) dirty = true;
        return batch.Samples.Length;
    }

    private string TrafficCounters()
    {
        string text = "\n" + Loc.F("Přenosy {0}/{1} · vytěsněno v okně {2}", trafficRows.Count, ProcessTrafficRows.MaximumRows, trafficRows.Trimmed)
            + "\n" + Loc.T("Odhad uchovaných dat: jednotlivé události do 3 MiB, přenosy do 3 MiB, životnosti do 2 MiB; podržený seznam do 3 MiB navíc.")
            + " " + Loc.T("Souhrn životností a jeho počty jsou od posledního vymazání; nejde o úplnou historii systému.");
        if (trafficInfo is not { } t) return text;
        return text + "\n" + Loc.F("Fyzický disk: {0} · síť: {1} · zdroj životností: {2} · epocha {3} · nepřiřazené události {4} / {5} B · zahozené čekající {6} · vytěsněné vzorky {7} · pozdní události {8} · zneplatnění přiřazení {9}",
                SampleText(t.DiskState), SampleText(t.NetworkState), SampleText(t.IdentityProviderState), t.AttributionEpoch,
                t.UnknownEvents, t.UnknownBytes, t.PendingDropped, t.EvictedSamples, t.LateEvents, t.IdentityConflicts)
            + "\n" + Loc.F("Nativní sdílený rozpočet kontejnerů a rámců {0}/{1} B; paměť Windows ETW je zvlášť.", t.BudgetBytesUsed, t.BudgetLimit);
    }

    private static string SampleText(SampleState value) => value switch {
        SampleState.Valid => Loc.T("dostupný"), SampleState.First => Loc.T("čeká na data"), SampleState.Gap => Loc.T("částečný proud"),
        SampleState.AccessDenied => Loc.T("přístup zamítnut"), SampleState.Unsupported => Loc.T("nepodporovaný"),
        SampleState.Stale => Loc.T("zastaralý"), SampleState.Error => Loc.T("chyba"), _ => Loc.T("nedostupný")
    };

    private void UpdateCommands()
    {
        bool closePending = closing || mainClosing;
        bool running = state is ProcessEventMonitorState.Starting or ProcessEventMonitorState.Running or ProcessEventMonitorState.Stopping;
        start.IsEnabled = !closePending && !running && (batchFlags & 2) == 0;
        stop.IsEnabled = !closePending && (running || (batchFlags & 2) != 0);
        clear.IsEnabled = !closePending;
    }

    internal Task<bool> StopForCloseAsync(bool fromMainWindow = false)
    {
        if (fromMainWindow) mainClosing = true; else closing = true;
        UpdateCommands();
        return closeStop is { IsCompleted: false } pending ? pending : closeStop = DrainForCloseAsync();
    }

    private async Task<bool> DrainForCloseAsync()
    {
        if (monitor is null) return true;
        try {
            monitor.Stop();
            long began = Stopwatch.GetTimestamp();
            do {
                int count = ReadBatch();
                if (count < 0) return false;
                // Stopped confirms session cleanup, not an empty lifecycle queue.
                if (!NeedsCleanup && count == 0) return true;
                if (NeedsCleanup) await Task.Delay(50);
                else await Task.Yield();
            } while (Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(5));
            return false;
        } catch (Exception error) when (InteropError(error)) {
            status.Text = Loc.F("Úklid záznamu nebyl potvrzený: {0}", error.Message); return false;
        }
    }

    internal bool NeedsCleanup => monitor is not null && ((batchFlags & 2) != 0
        || state is ProcessEventMonitorState.Starting or ProcessEventMonitorState.Running or ProcessEventMonitorState.Stopping);
    internal void CloseAfterStop() { allowClose = true; if (!closed) Close(); }
    internal void CancelClose(bool fromMainWindow = false)
    {
        if (fromMainWindow) mainClosing = false; else closing = false;
        UpdateCommands();
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose || closed) return;
        if (uiCheckRunning) { args.Cancel = true; uiCheckCancellation?.Cancel(); return; }
        args.Cancel = true;
        if (closing || mainClosing) return;
        closing = true; UpdateCommands();
        bool clean = await StopForCloseAsync();
        if (closed) return;
        if (!clean) {
            var dialog = new ContentDialog { XamlRoot = layout.XamlRoot, Title = Loc.T("Zastavení a dočtení záznamu nebylo potvrzené"), CloseButtonText = Loc.T("Ponechat otevřené"),
                PrimaryButtonText = Loc.T("Zavřít i tak"), Content = Loc.T("Nepodařilo se potvrdit úklid vlastní relace ETW nebo dočíst její události. Relace může dál zaznamenávat a při zavření se mohou ztratit dosud nepřečtené události. Okno můžete ponechat otevřené a zkusit zastavení znovu.") };
            Appearance.Dialog(dialog);
            try { clean = await dialog.ShowAsync() == ContentDialogResult.Primary; }
            catch (InvalidOperationException) { clean = false; }
        }
        if (clean) { allowClose = true; DispatcherQueue.TryEnqueue(() => Close()); } else CancelClose();
    }

    private static string StateText(ProcessEventMonitorState value) => value switch {
        ProcessEventMonitorState.Idle => Loc.T("Záznam není spuštěný"), ProcessEventMonitorState.Starting => Loc.T("Spouštím vlastní relaci"),
        ProcessEventMonitorState.Running => Loc.T("Záznam běží"), ProcessEventMonitorState.Stopping => Loc.T("Zastavuji vlastní relaci"),
        ProcessEventMonitorState.Stopped => Loc.T("Záznam zastavený"), ProcessEventMonitorState.AccessDenied => Loc.T("Přístup k ETW byl zamítnutý; aplikace pokračuje bez něj"),
        ProcessEventMonitorState.Unsupported => Loc.T("Zdroj ETW není na tomto systému podporovaný"), ProcessEventMonitorState.Busy => Loc.T("Předchozí relace se ještě uklízí"),
        ProcessEventMonitorState.StopTimedOut => Loc.T("Zastavení překročilo časový limit"), ProcessEventMonitorState.CleanupFailed => Loc.T("Windows nepotvrdil úklid relace"),
        _ => Loc.T("Záznam skončil chybou")
    };
    private static bool InteropError(Exception error) => error is IOException or InvalidDataException or NativeMonitorException or EntryPointNotFoundException or DllNotFoundException
        or BadImageFormatException or InvalidOperationException or ArgumentException;
    private static Button Command(string text) => new() { Content = text, MinHeight = 32, Padding = new Thickness(12, 5, 12, 5) };
    private static TextBlock Text(string text) => new() { Text = text, FontFamily = UiTypography.Font, FontSize = 12, Foreground = Display.Brush(0x95a4ad) };
    private static void Identify(DependencyObject item, string id, string name) { AutomationProperties.SetAutomationId(item, id); AutomationProperties.SetName(item, name); }
}
