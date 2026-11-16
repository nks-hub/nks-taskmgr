using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;
using Windows.ApplicationModel.DataTransfer;

namespace NksTaskMgr.WinUI;

/// <summary>"Spuštěné a ukončené procesy": one table of process starts and ends from the application-wide timeline.</summary>
internal sealed partial class ProcessEventsWindow : Window
{
    private const double TableMinWidth = 1100;
    private readonly Grid layout = new() { Padding = new Thickness(16, 12, 16, 12), RowSpacing = 8, Background = Display.Brush(0x171d21) };
    private readonly TextBlock status = Text("");
    private readonly Button elevate = new() { Content = Loc.T("Spustit jako správce"), MinHeight = 0, Height = 28, Padding = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBox search = new() { PlaceholderText = Loc.T("Hledat název, PID nebo příkazový řádek"), MinHeight = 32 };
    private readonly ComboBox kinds = new() { Width = 200, MinHeight = 32 };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly ProcessEventRows visible = [];
    private readonly ProcessEventTemplates templates = new();
    private readonly Dictionary<(ProcessTimelineEntry, bool), ProcessEventRow> cache = [];
    private readonly DispatcherQueueTimer timer;
    private readonly ProcessTimeline timeline;
    private readonly ProcessCapture? capture;
    private int shownVersion = -1;
    private bool closed;
    private MenuFlyout? menu;
    private ScrollViewer? scroller;

    /// <summary>Set by the main window: restarts the application elevated so the ETW capture can start.</summary>
    internal Action? RequestElevation { get; set; }
    /// <summary>Set by the main window: selects the live process in Procesy; false when it is gone.</summary>
    internal Func<ProcessKey, bool>? RevealProcess { get; set; }

    internal ProcessEventsWindow(ProcessTimeline timeline, ProcessCapture? capture)
    {
        this.timeline = timeline; this.capture = capture;
        Title = Loc.T("Spuštěné a ukončené procesy") + " · NKS Task Manager";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.ResizeDip(1280, 760); Appearance.Attach(this, layout);
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) layout.RowDefinitions.Add(new RowDefinition { Height = height });

        var messages = new Grid { ColumnSpacing = 10, MinHeight = 32 };
        messages.ColumnDefinitions.Add(new ColumnDefinition()); messages.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.TextWrapping = TextWrapping.Wrap; status.VerticalAlignment = VerticalAlignment.Center;
        elevate.Click += (_, _) => RequestElevation?.Invoke();
        ToolTipService.SetToolTip(elevate, Loc.T("Aplikace se spustí znovu jako správce po potvrzení výzvy UAC."));
        messages.Children.Add(status); messages.Children.Add(elevate); Grid.SetColumn(elevate, 1);
        layout.Children.Add(messages);

        var filters = new Grid { ColumnSpacing = 8 };
        filters.ColumnDefinitions.Add(new ColumnDefinition()); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var name in new[] { Loc.T("Vše"), Loc.T("Spuštěné"), Loc.T("Ukončené"), Loc.T("Opakovaně spouštěné") }) kinds.Items.Add(name);
        kinds.SelectedIndex = 0;
        filters.Children.Add(search); filters.Children.Add(kinds); Grid.SetColumn(kinds, 1); Grid.SetRow(filters, 1); layout.Children.Add(filters);

        var table = new Grid { MinWidth = TableMinWidth };
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); table.RowDefinitions.Add(new RowDefinition());
        var headings = new Grid { Padding = new Thickness(10, 6, 10, 6), ColumnSpacing = 12, Background = Display.Brush(0x1d252a) };
        foreach (var width in new[] { 62d, 72, 170, 56, 190, -1, 86, 100, 80 }) headings.ColumnDefinitions.Add(new ColumnDefinition { Width = width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
        var names = new[] { Loc.T("Čas"), Loc.T("Událost"), Loc.T("Proces"), "PID", Loc.T("Rodič"), Loc.T("Příkazový řádek"), Loc.T("Doba běhu"), Loc.T("Návratový kód"), Loc.T("Opakování") };
        for (int i = 0; i < names.Length; ++i) { var label = Text(names[i]); Grid.SetColumn(label, i); headings.Children.Add(label); }
        ToolTipService.SetToolTip(headings.Children[7], Loc.T("Kód, kterým proces skončil. Zná ho jen sledování ETW, které vyžaduje správce."));
        ToolTipService.SetToolTip(headings.Children[8], Loc.T("Kolikrát se stejný proces se stejným rodičem spustil během 60 sekund, od třetího spuštění."));
        table.Children.Add(headings);
        // Rows arrive continuously; the fade-in and reposition animations would leave the list half transparent.
        list.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
        list.ItemTemplate = (DataTemplate)templates["ProcessEvent"]; list.ItemsSource = visible;
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30d));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); list.ItemContainerStyle = style;
        Grid.SetRow(list, 1); table.Children.Add(list);
        var scroll = new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        scroll.SizeChanged += (_, args) => table.Width = Math.Max(TableMinWidth, args.NewSize.Width);
        Grid.SetRow(scroll, 2); layout.Children.Add(scroll);
        Content = layout;

        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(500); timer.Tick += (_, _) => Refresh();
        search.TextChanged += (_, _) => Refresh(true); kinds.SelectionChanged += (_, _) => Refresh(true);
        list.ContextRequested += OnContextRequested;
        Closed += (_, _) => { closed = true; timer.Stop(); menu?.Hide(); };
        Identify(status, "events-status", Loc.T("Stav sledování procesů")); Identify(search, "events-filter", Loc.T("Hledat v událostech procesů"));
        Identify(kinds, "events-kind", Loc.T("Druh události")); Identify(list, "events-list", Loc.T("Spuštěné a ukončené procesy")); Identify(elevate, "events-elevate", Loc.T("Spustit jako správce"));
        Refresh(true); timer.Start();
    }

    internal IReadOnlyList<ProcessEventRow> Rows => visible;

    private ProcessEventFilter Filter => (ProcessEventFilter)Math.Max(0, kinds.SelectedIndex);

    internal void Refresh(bool force = false)
    {
        if (closed) return;
        UpdateStatus();
        if (!force && shownVersion == timeline.Version) return;
        shownVersion = timeline.Version;
        var selected = list.SelectedItem as ProcessEventRow;
        var rows = ProcessEventRow.Build(timeline.Entries, Filter, search.Text.Trim(), cache);
        if (cache.Count > 4 * ProcessTimeline.MaximumEntries) cache.Clear();
        scroller ??= Descendant(list);
        bool atTop = scroller is null || scroller.VerticalOffset < 1;
        visible.Apply(rows);
        // Newest rows are inserted above; a list the user has not scrolled stays on them.
        if (atTop) scroller?.ChangeView(null, 0, null, true);
        if (selected is not null && rows.FirstOrDefault(row => row.Entry == selected.Entry && row.Started == selected.Started) is var replacement && !ReferenceEquals(replacement, list.SelectedItem))
            list.SelectedItem = replacement;
    }

    private void UpdateStatus()
    {
        bool etw = capture is { State: ProcessEventMonitorState.Running };
        string tracking = Loc.F("Sleduji od {0}.", timeline.ObservingSince.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)) + " ";
        status.Text = tracking + (etw ? Loc.T("Běží i sledování ETW, takže znám návratové kódy a zachytím i velmi krátké procesy.")
            : MainWindow.RunningElevated || capture is { Active: true } ? Loc.F("Sledování ETW neběží ({0}), proto chybí návratové kódy a velmi krátké procesy.",
                capture is { Error.Length: > 0 } ? capture.Error : StateText(capture?.State ?? ProcessEventMonitorState.Idle))
            : Loc.T("Bez práv správce nejsou vidět návratové kódy a procesy, které skončily do asi jedné sekundy po spuštění."));
        var visibility = !MainWindow.RunningElevated && RequestElevation is not null ? Visibility.Visible : Visibility.Collapsed;
        if (elevate.Visibility != visibility) elevate.Visibility = visibility;
        ToolTipService.SetToolTip(status, capture?.Info is { } info
            ? Loc.F("Ztracené události ETW: {0} · ztracené buffery: {1} · přeplněná fronta: {2} · chyby dekódování: {3}. Při ztrátě mohou v seznamu chybět procesy.",
                info.ProviderEventsLost, info.ProviderBuffersLost, info.QueueDropped, info.DecodeErrors) : null);
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var node = args.OriginalSource as DependencyObject;
        while (node is not null && node is not ListViewItem) node = VisualTreeHelper.GetParent(node);
        var row = (node as ListViewItem)?.Content as ProcessEventRow ?? list.SelectedItem as ProcessEventRow;
        if (row is null) return;
        list.SelectedItem = row;
        menu?.Hide(); menu = BuildMenu(row);
        if (args.TryGetPosition(list, out var point)) menu.ShowAt(list, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(list.ContainerFromItem(row) as FrameworkElement ?? list);
        args.Handled = true;
    }

    internal MenuFlyout BuildMenu(ProcessEventRow row)
    {
        var result = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = Loc.T("Kopírovat řádek") };
        AutomationProperties.SetAutomationId(copy, "events-copy");
        copy.Click += (_, _) => { var package = new DataPackage(); package.SetText(row.Line); Clipboard.SetContent(package); };
        result.Items.Add(copy);
        if (RevealProcess is { } reveal && row.Entry.Key is { } key && timeline.IsAlive(key)) {
            var show = new MenuFlyoutItem { Text = Loc.T("Zobrazit v Procesech") };
            AutomationProperties.SetAutomationId(show, "events-reveal");
            show.Click += (_, _) => reveal(key);
            result.Items.Add(show);
        }
        return result;
    }

    private static ScrollViewer? Descendant(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); ++i) {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer found || (found = Descendant(child)!) is not null) return found;
        }
        return null;
    }

    private static string StateText(ProcessEventMonitorState value) => value switch {
        ProcessEventMonitorState.Idle => Loc.T("nespuštěno"), ProcessEventMonitorState.Starting => Loc.T("spouští se"),
        ProcessEventMonitorState.Running => Loc.T("běží"), ProcessEventMonitorState.Stopping => Loc.T("zastavuje se"),
        ProcessEventMonitorState.Stopped => Loc.T("zastaveno"), ProcessEventMonitorState.AccessDenied => Loc.T("přístup odepřen"),
        ProcessEventMonitorState.Unsupported => Loc.T("na tomto systému nepodporováno"), ProcessEventMonitorState.Busy => Loc.T("předchozí relace se ještě uklízí"),
        ProcessEventMonitorState.StopTimedOut => Loc.T("zastavení trvalo příliš dlouho"), ProcessEventMonitorState.CleanupFailed => Loc.T("Windows nepotvrdil úklid"),
        _ => Loc.T("chyba")
    };
    private static TextBlock Text(string text) => new() { Text = text, FontFamily = UiTypography.Font, FontSize = 12, Foreground = Display.Brush(0x95a4ad) };
    private static void Identify(DependencyObject item, string id, string name) { AutomationProperties.SetAutomationId(item, id); AutomationProperties.SetName(item, name); }
}
