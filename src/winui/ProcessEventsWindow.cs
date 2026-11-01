using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;
using Windows.Graphics;

namespace NksTaskMgr.WinUI;

internal sealed class ProcessEventsWindow : Window
{
    private const int MaximumRows = 10000;
    private const int MaximumEstimatedBytes = 12 * 1024 * 1024;
    private readonly Grid layout = new() { Padding = new Thickness(16), RowSpacing = 8, Background = Display.Brush(0x171d21) };
    private readonly Button start = Command("Spustit nový záznam"), stop = Command("Zastavit"), clear = Command("Vymazat seznam");
    private readonly TextBlock status = Text("Záznam není spuštěný."), counters = Text(""), notice = Text("ETW sleduje vznik a ukončení procesů až od spuštění záznamu. Historické řádky jsou pouze pro čtení.");
    private readonly TextBox search = new() { PlaceholderText = "Filtrovat název nebo PID", MinWidth = 180, MinHeight = 32 };
    private readonly ComboBox kinds = new() { Width = 145, MinHeight = 32 };
    private readonly CheckBox hold = new() { Content = "Podržet seznam" }, follow = new() { Content = "Sledovat konec", IsChecked = true };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0) };
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12, Text = "Vyberte událost." };
    private readonly List<ProcessEventRow> retained = [];
    private readonly ProcessEventRows visible = [];
    private readonly DispatcherQueueTimer timer, filterTimer;
    private readonly Func<(long Time, BootIdentifier Boot, SampleState State)> currentProcesses;
    private NativeEventMonitor? monitor;
    private ProcessEventMonitorState state = ProcessEventMonitorState.Idle;
    private uint batchFlags;
    private ulong trimmed, resyncVersion;
    private int estimatedBytes;
    private long originQpc, resyncAfter;
    private bool closed, closing, allowClose, dirty;

    internal ProcessEventsWindow(Func<(long Time, BootIdentifier Boot, SampleState State)> currentProcesses)
    {
        this.currentProcesses = currentProcesses;
        Title = "Události procesů · NKS Task Manager";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.Resize(new SizeInt32(1120, 780)); Appearance.Attach(this, layout);
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(155) })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        commands.Children.Add(start); commands.Children.Add(stop); commands.Children.Add(clear); commands.Children.Add(hold); commands.Children.Add(follow);
        layout.Children.Add(new ScrollViewer { Content = commands, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var information = new StackPanel { Spacing = 4 };
        foreach (var text in new[] { status, counters, notice }) { text.TextWrapping = TextWrapping.Wrap; text.IsTextSelectionEnabled = true; information.Children.Add(text); }
        var messages = new ScrollViewer { Content = information, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(messages, 1); layout.Children.Add(messages);
        var filters = new Grid { ColumnSpacing = 8 };
        filters.ColumnDefinitions.Add(new ColumnDefinition()); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters.Children.Add(search); filters.Children.Add(kinds); Grid.SetColumn(kinds, 1); Grid.SetRow(filters, 2); layout.Children.Add(filters);
        foreach (var name in new[] { "Všechny události", "Vznik procesů", "Ukončení procesů" }) kinds.Items.Add(name);
        kinds.SelectedIndex = 0;
        var templates = new ProcessEventTemplates(); list.ItemTemplate = (DataTemplate)templates["ProcessEvent"]; list.ItemsSource = visible;
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        list.ItemContainerStyle = style; Grid.SetRow(list, 3); layout.Children.Add(list);
        ScrollViewer.SetVerticalScrollBarVisibility(detail, ScrollBarVisibility.Auto); Grid.SetRow(detail, 4); layout.Children.Add(detail);
        Content = layout;
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(250); timer.Tick += (_, _) => ReadBatch();
        filterTimer = DispatcherQueue.CreateTimer(); filterTimer.Interval = TimeSpan.FromMilliseconds(150); filterTimer.IsRepeating = false; filterTimer.Tick += (_, _) => RefreshRows();
        start.Click += (_, _) => StartCapture(); stop.Click += (_, _) => StopCapture(); clear.Click += (_, _) => ClearRows();
        search.TextChanged += (_, _) => { filterTimer.Stop(); filterTimer.Start(); };
        kinds.SelectionChanged += (_, _) => RefreshRows(); hold.Unchecked += (_, _) => RefreshRows();
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ProcessEventRow row) detail.Text = row.Detail; };
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => { closed = true; timer.Stop(); filterTimer.Stop(); monitor?.Dispose(); monitor = null; };
        Identify(start, "events-start", "Spustit vlastní záznam ETW"); Identify(stop, "events-stop", "Zastavit vlastní záznam ETW");
        Identify(clear, "events-clear", "Vymazat zobrazené události"); Identify(search, "events-filter", "Filtrovat zaznamenané události");
        Identify(kinds, "events-kind", "Druh události"); Identify(list, "events-list", "Historické události, pouze pro čtení");
        Identify(detail, "events-detail", "Podrobnosti historické události"); UpdateCommands();
    }

    private void StartCapture()
    {
        try {
            monitor ??= NativeEventMonitor.Open();
            // Starting a new log also discards at most one full old native queue.
            for (int i = 0; i < 8; ++i) { using var previous = monitor.Read(); }
            state = ProcessEventMonitorState.Starting; monitor.Start();
            ClearRows(); resyncVersion = 0; resyncAfter = 0; originQpc = 0;
            timer.Start(); ReadBatch();
        } catch (Exception error) when (InteropError(error)) { status.Text = "Záznam nelze spustit: " + error.Message; }
    }

    private void StopCapture()
    {
        try { monitor?.Stop(); ReadBatch(); }
        catch (Exception error) when (InteropError(error)) { status.Text = "Zastavení nebylo potvrzené: " + error.Message; }
    }

    private void ReadBatch()
    {
        if (closed || monitor is null) return;
        try {
            using var frame = monitor.Read();
            var info = frame.Info;
            CapabilityReports.ObserveEvents(info);
            state = info.State; batchFlags = (uint)info.Flags;
            var events = frame.CopyEvents();
            if (events.Length > 0) {
                if (originQpc == 0) originQpc = events[0].TimestampQpc;
                foreach (var item in events) {
                    var row = new ProcessEventRow(item, info.QpcFrequency, originQpc);
                    retained.Add(row); estimatedBytes += row.EstimatedBytes;
                }
                int remove = 0;
                while (remove < retained.Count && (retained.Count - remove > MaximumRows || estimatedBytes > MaximumEstimatedBytes))
                    estimatedBytes -= retained[remove++].EstimatedBytes;
                if (remove > 0) { retained.RemoveRange(0, remove); trimmed += (ulong)remove; }
                dirty = true;
            }
            status.Text = StateText(state) + (info.Win32Error != 0 ? " · Windows " + info.Win32Error : "");
            counters.Text = "Uchováno " + retained.Count + "/" + MaximumRows + " · vytěsněno v okně " + trimmed
                + " · ztracené události ETW " + info.ProviderEventsLost + " · ztracené buffery " + info.ProviderBuffersLost
                + " · přeplnění fronty " + info.QueueDropped + " · chyby dekódování " + info.DecodeErrors + " · nejasné záznamy " + info.Ambiguous;
            notice.Text = "Čas v seznamu je relativní čas ETW. Identita životnosti není ověřená; nad těmito řádky se neprovádějí živé akce. Nové spuštění nahradí seznam.";
            if ((batchFlags & 1) != 0) {
                if (resyncVersion != info.ResyncVersion) { resyncVersion = info.ResyncVersion; resyncAfter = NativeMonitor.MonotonicNowNs; }
                var current = currentProcesses();
                if (info.BootState == SampleState.Valid && current.State == SampleState.Valid && current.Boot == info.Boot && current.Time > resyncAfter)
                    monitor.AcknowledgeResync(info.ResyncVersion);
                else notice.Text += " Základní seznam čeká na nové ověření po změně nebo ztrátě zdroje.";
            }
            if ((batchFlags & 2) != 0) notice.Text += " Relace ještě není uklizená.";
            if (dirty && hold.IsChecked != true) RefreshRows();
            UpdateCommands();
        } catch (Exception error) when (InteropError(error)) { timer.Stop(); status.Text = "Čtení událostí selhalo: " + error.Message; }
    }

    private void ClearRows()
    {
        retained.Clear(); visible.Apply([]); estimatedBytes = 0; trimmed = 0; dirty = false; detail.Text = "Vyberte událost.";
    }

    private void RefreshRows()
    {
        if (closed || hold.IsChecked == true) { dirty = true; return; }
        var selected = list.SelectedItem as ProcessEventRow;
        string query = search.Text.Trim(); int kind = kinds.SelectedIndex;
        var values = retained.Where(row => (kind == 0 || (int)row.Source.Kind == kind)
            && (query.Length == 0 || row.Matches(query))).ToArray();
        visible.Apply(values); dirty = false;
        if (selected is not null && values.Contains(selected)) list.SelectedItem = selected;
        else if (selected is not null) detail.Text = "Vybraná událost už není v aktuálním seznamu.";
        if (follow.IsChecked == true && selected is null && values.Length > 0) list.ScrollIntoView(values[^1]);
    }

    private void UpdateCommands()
    {
        bool running = state is ProcessEventMonitorState.Starting or ProcessEventMonitorState.Running or ProcessEventMonitorState.Stopping;
        start.IsEnabled = !closing && !running && (batchFlags & 2) == 0;
        stop.IsEnabled = !closing && (running || (batchFlags & 2) != 0);
    }

    internal async Task<bool> StopForCloseAsync()
    {
        if (monitor is null) return true;
        try {
            monitor.Stop();
            ReadBatch();
            if (!NeedsCleanup) return true;
            await monitor.WaitForStopAsync(TimeSpan.FromSeconds(5));
            ReadBatch();
            return !NeedsCleanup;
        } catch (Exception error) when (InteropError(error)) {
            status.Text = "Úklid záznamu nebyl potvrzený: " + error.Message; return false;
        }
    }

    internal bool NeedsCleanup => monitor is not null && ((batchFlags & 2) != 0
        || state is ProcessEventMonitorState.Starting or ProcessEventMonitorState.Running or ProcessEventMonitorState.Stopping);
    internal void CloseAfterStop() { allowClose = true; if (!closed) Close(); }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose || closed) return;
        args.Cancel = true;
        if (closing) return;
        closing = true; UpdateCommands();
        bool clean = await StopForCloseAsync();
        if (closed) return;
        if (!clean) {
            var dialog = new ContentDialog { XamlRoot = layout.XamlRoot, Title = "Zastavení záznamu nebylo potvrzené", CloseButtonText = "Ponechat otevřené",
                PrimaryButtonText = "Zavřít i tak", Content = "Windows nepotvrdil ukončení vlastní relace ETW. Může dál zaznamenávat události. Okno můžete ponechat otevřené a zkusit zastavení znovu." };
            Appearance.Dialog(dialog);
            try { clean = await dialog.ShowAsync() == ContentDialogResult.Primary; }
            catch (InvalidOperationException) { clean = false; }
        }
        if (clean) { allowClose = true; DispatcherQueue.TryEnqueue(() => Close()); } else { closing = false; UpdateCommands(); }
    }

    private static string StateText(ProcessEventMonitorState value) => value switch {
        ProcessEventMonitorState.Idle => "Záznam není spuštěný", ProcessEventMonitorState.Starting => "Spouštím vlastní relaci",
        ProcessEventMonitorState.Running => "Záznam běží", ProcessEventMonitorState.Stopping => "Zastavuji vlastní relaci",
        ProcessEventMonitorState.Stopped => "Záznam zastavený", ProcessEventMonitorState.AccessDenied => "Přístup k ETW byl zamítnutý; aplikace pokračuje bez něj",
        ProcessEventMonitorState.Unsupported => "Zdroj ETW není na tomto systému podporovaný", ProcessEventMonitorState.Busy => "Předchozí relace se ještě uklízí",
        ProcessEventMonitorState.StopTimedOut => "Zastavení překročilo časový limit", ProcessEventMonitorState.CleanupFailed => "Windows nepotvrdil úklid relace",
        _ => "Záznam skončil chybou"
    };
    private static bool InteropError(Exception error) => error is IOException or InvalidDataException or NativeMonitorException or EntryPointNotFoundException or DllNotFoundException
        or BadImageFormatException or InvalidOperationException or ArgumentException;
    private static Button Command(string text) => new() { Content = text, MinHeight = 32, Padding = new Thickness(12, 5, 12, 5) };
    private static TextBlock Text(string text) => new() { Text = text, FontFamily = UiTypography.Font, FontSize = 12, Foreground = Display.Brush(0x95a4ad) };
    private static void Identify(DependencyObject item, string id, string name) { AutomationProperties.SetAutomationId(item, id); AutomationProperties.SetName(item, name); }
}
