using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Grid applicationWorkspace = new() { ColumnSpacing = 8 };
    private readonly ListView applicationGroups = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0) };
    private readonly ApplicationGroupTemplates applicationTemplates = new();
    private readonly ComboBox applicationChoice = new() { DisplayMemberPath = nameof(ApplicationGroupRow.Title), Width = 180, MaxWidth = 180, MinWidth = 0, MinHeight = 28, Height = 28, FontSize = 12, FontFamily = UiTypography.Font };
    private readonly Grid applicationToolbar = new() { ColumnSpacing = 4 };
    private readonly DropDownButton applicationSort = new() { MinHeight = 28, Height = 28, FontSize = 11, FontFamily = UiTypography.Font, Padding = new Thickness(6, 0, 6, 0) };
    private readonly Button applicationInfo = Button(Loc.T("Info"));
    private readonly TextBlock applicationStatus = new() { FontSize = 11, FontFamily = UiTypography.Font, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock applicationTitle = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontFamily = UiTypography.Font,
        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock applicationSummary = new() { FontSize = 12, FontFamily = UiTypography.Font, Foreground = Display.Brush(0x95a4ad), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel applicationHeader = new() { Spacing = 2, Padding = new Thickness(4, 0, 0, 6), Visibility = Visibility.Collapsed };
    private readonly TextBlock applicationFacts = new() { FontSize = 12, FontFamily = UiTypography.Font, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
    private readonly CancellationTokenSource applicationLifetime = new();
    private ProcessKey? autoSelectedApplication;
    private CancellationTokenSource? applicationRequest;
    private bool updatingApplicationChoice, applicationsClosed, applicationInfoOpen;
    private long nextApplicationRead;
    private ProcessKey? pendingApplicationSelection;
    private ProcessNavigationState? processNavigation, applicationNavigation;
    private ProcessNavigationState? pendingNavigationRestore;
    private EventHandler<object>? navigationRestoreFrame;

    private sealed record ProcessNavigationState(ProcessMode Mode, string Query, int SortColumn, bool Descending,
        ProcessKey[] Collapsed, ProcessKey[] Selection, ProcessKey? Primary, ProcessKey? Application,
        ProcessKey? Anchor, double AnchorTop, double VerticalOffset, double HorizontalOffset,
        ProcessKey? GroupAnchor = null, double GroupTop = 0, double GroupOffset = 0);

    private FrameworkElement BuildApplicationGroups(FrameworkElement table)
    {
        applicationWorkspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) }); applicationWorkspace.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new Grid();
        sidebar.Children.Add(applicationGroups);
        applicationGroups.ItemsSource = applicationChoice.ItemsSource = model.Applications.Visible;
        applicationGroups.ItemTemplate = (DataTemplate)applicationTemplates["ApplicationGroup"];
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 48d)); style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 48d));
        applicationGroups.ItemContainerStyle = style;
        applicationWorkspace.Children.Add(sidebar);
        applicationToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); applicationToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        applicationToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        applicationToolbar.Children.Add(applicationChoice); applicationToolbar.Children.Add(applicationSort); Grid.SetColumn(applicationSort, 1);
        applicationInfo.Height = applicationInfo.MinHeight = 28; applicationInfo.Width = 32; applicationInfo.FontSize = 14; applicationInfo.Padding = new Thickness(0);
        applicationInfo.Content = "ⓘ";
        applicationToolbar.Children.Add(applicationInfo); Grid.SetColumn(applicationInfo, 2);
        applicationSort.Flyout = ApplicationSortMenu();
        AutomationProperties.SetName(applicationSort, Loc.T("Řadit skupiny aplikací podle")); AutomationProperties.SetAutomationId(applicationSort, "application-sort");
        ToolTipService.SetToolTip(applicationSort, Loc.T("Řazení skupin aplikací; paměť je součet pracovních sad dostupných procesů."));
        var pane = new Grid();
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); pane.RowDefinitions.Add(new RowDefinition());
        applicationHeader.Children.Add(applicationTitle); applicationHeader.Children.Add(applicationSummary);
        AutomationProperties.SetAutomationId(applicationHeader, "application-header");
        pane.Children.Add(applicationHeader); pane.Children.Add(table); Grid.SetRow(table, 1);
        applicationWorkspace.Children.Add(pane); Grid.SetColumn(pane, 1);
        BuildApplicationTable();
        applicationWorkspace.Children.Add(appsPane); Grid.SetColumnSpan(appsPane, 2);
        var information = new StackPanel { Spacing = 8, MaxWidth = 420 };
        information.Children.Add(applicationStatus); information.Children.Add(applicationFacts);
        var info = new Flyout { Content = new ScrollViewer { Content = information, MaxHeight = 320, Padding = new Thickness(0, 0, 20, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        info.Opening += (_, _) => { applicationInfoOpen = true; applicationFacts.Text = ApplicationFacts(); };
        info.Closed += (_, _) => applicationInfoOpen = false;
        applicationInfo.Flyout = info;
        AutomationProperties.SetName(applicationInfo, Loc.T("Informace o skupině aplikace")); AutomationProperties.SetAutomationId(applicationInfo, "application-info");
        ToolTipService.SetToolTip(applicationInfo, Loc.T("Informace o vybrané skupině a dostupnosti přehledu."));
        void Select(object? value) {
            if (updatingApplicationChoice || model.Applications.Updating || model.Mode != ProcessMode.Applications || value is not ApplicationGroupRow group) return;
            if (!model.Processes.Any(row => row.Key == group.Key && model.IsCurrent(row))) return;
            model.SelectApplication(group.Key); pendingApplicationSelection = null; RefreshProcesses();
            SelectApplicationRoot(group);
        }
        applicationGroups.SelectionChanged += (_, _) => Select(applicationGroups.SelectedItem);
        applicationChoice.SelectionChanged += (_, _) => Select(applicationChoice.SelectedItem);
        AutomationProperties.SetName(applicationGroups, Loc.T("Skupiny aplikací podle ověřených oken")); AutomationProperties.SetAutomationId(applicationGroups, "application-groups");
        AutomationProperties.SetName(applicationChoice, Loc.T("Vybrat skupinu aplikace")); AutomationProperties.SetAutomationId(applicationChoice, "application-choice");
        var releaseView = PreserveApplicationGroupView(model.Applications, applicationGroups, applicationChoice);
        Closed += (_, _) => { CancelNavigationRestore(); releaseView(); applicationsClosed = true; applicationRequest?.Cancel(); applicationLifetime.Cancel(); if (applicationRequest is null) applicationLifetime.Dispose(); };
        ResizeApplicationGroups();
        return applicationWorkspace;
    }

    private static string ApplicationSortTitle(ApplicationGroupSort sort) => sort switch {
        ApplicationGroupSort.Cpu => "CPU", ApplicationGroupSort.Memory => Loc.T("Paměť"), ApplicationGroupSort.Processes => Loc.T("Procesy"), _ => Loc.T("Název")
    };

    private MenuFlyout ApplicationSortMenu()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => {
            menu.Items.Clear();
            void SetSort(ApplicationGroupSort sort, bool descending) { model.Applications.SetSort(sort, descending); RefreshApplicationGroups(); }
            foreach (var sort in Enum.GetValues<ApplicationGroupSort>()) {
                var item = new ToggleMenuFlyoutItem { Text = ApplicationSortTitle(sort), IsChecked = model.Applications.Sort == sort };
                item.Click += (_, _) => SetSort(sort, model.Applications.Sort == sort ? model.Applications.SortDescending : sort != ApplicationGroupSort.Name);
                menu.Items.Add(item);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (bool descending in new[] { false, true }) {
                var item = new ToggleMenuFlyoutItem { Text = descending ? Loc.T("Sestupně") : Loc.T("Vzestupně"), IsChecked = model.Applications.SortDescending == descending };
                item.Click += (_, _) => SetSort(model.Applications.Sort, descending);
                menu.Items.Add(item);
            }
        };
        return menu;
    }

    private void SelectProcessMode(ProcessMode mode)
    {
        var parked = pendingNavigationRestore;
        CancelNavigationRestore();
        bool entering = mode == ProcessMode.Applications && model.Mode != mode;
        bool leaving = model.Mode == ProcessMode.Applications && mode != model.Mode;
        if (!entering && !leaving) { model.SetMode(mode); RefreshProcesses(); return; }
        if (entering) {
            processNavigation = parked is { Mode: not ProcessMode.Applications } && NavigationModelMatches(parked)
                ? parked : CaptureProcessNavigation();
            pendingApplicationSelection = applicationNavigation is null ? processNavigation.Primary : null;
        } else {
            applicationNavigation = CaptureProcessNavigation();
            applicationRequest?.Cancel(); pendingApplicationSelection = null;
        }
        var state = entering ? applicationNavigation : processNavigation;
        state ??= new(mode, "", 0, false, [], [], null, null, null, 0, 0, 0);
        state = state with { Mode = mode };
        processQuery = state.Query; SetNavigationSearchText(state.Query);
        model.RestoreProcessNavigation(mode, state.Query, state.SortColumn, state.Descending, state.Application, state.Collapsed);
        processTable.SetSort(model.SortColumn, model.SortDescending);
        RefreshProcesses(); RestoreProcessNavigation(state);
    }

    private void SetNavigationSearchText(string text)
    {
        if (search.Text == text) return;
        navigationSearchText = text; search.Text = text;
    }

    private void ShowApplications(bool visible)
    {
        if (visible) SelectProcessMode(ProcessMode.Applications);
        else if (model.Mode == ProcessMode.Applications) SelectProcessMode(processNavigation?.Mode ?? ProcessMode.All);
    }

    private static bool HasNavigationIdentity(ProcessRow row) => !row.Key.Boot.IsEmpty
        && row.Key.Kind != GenerationKind.Missing && row.Key.Generation != 0;

    private ProcessNavigationState CaptureProcessNavigation()
    {
        var scroll = Descendant<ScrollViewer>(processList);
        var selected = processList.SelectedItems.OfType<ProcessSlot>().Select(slot => slot.Row)
            .Where(row => model.IsCurrent(row) && HasNavigationIdentity(row)).Select(row => row.Key).ToArray();
        var primary = SelectedProcess is { } row && model.IsCurrent(row) && HasNavigationIdentity(row) ? row.Key : (ProcessKey?)null;
        var anchor = FirstVisibleProcess();
        var group = model.Mode == ProcessMode.Applications ? FirstVisibleApplicationGroup(applicationGroups) : null;
        string query = view is DesktopView.Processes or DesktopView.Applications ? search.Text : processQuery;
        return new(model.Mode, query, model.SortColumn, model.SortDescending, model.NavigationCollapsed, selected, primary,
            model.Mode == ProcessMode.Applications ? model.Applications.Selected?.Key : null,
            anchor?.Row.Key, anchor?.Top ?? 0, scroll?.VerticalOffset ?? 0, processTable.HorizontalScroll.HorizontalOffset,
            group?.Row.Key, group?.Top ?? 0, Descendant<ScrollViewer>(applicationGroups)?.VerticalOffset ?? 0);
    }

    private (ProcessRow Row, double Top)? FirstVisibleProcess()
    {
        if (processPage.Visibility != Visibility.Visible || processList.ItemsPanelRoot is not { } panel) return null;
        (ProcessRow Row, double Top)? first = null;
        foreach (var child in panel.Children) {
            if (child is not FrameworkElement { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } container
                || processList.ItemFromContainer(container) is not ProcessSlot slot) continue;
            int index = processList.IndexFromContainer(container);
            if (index < 0 || index >= processList.Items.Count || !ReferenceEquals(processList.Items[index], slot)
                || !model.IsCurrent(slot.Row) || !HasNavigationIdentity(slot.Row)) continue;
            var bounds = container.TransformToVisual(processList).TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (!double.IsFinite(bounds.Top) || !double.IsFinite(bounds.Bottom) || bounds.Bottom <= 0
                || bounds.Top >= processList.ActualHeight || bounds.Right <= 0 || bounds.Left >= processList.ActualWidth) continue;
            if (first is null || bounds.Top < first.Value.Top) first = (slot.Row, bounds.Top);
        }
        return first;
    }

    private void CancelNavigationRestore()
    {
        if (navigationRestoreFrame is not null) CompositionTarget.Rendering -= navigationRestoreFrame;
        navigationRestoreFrame = null; pendingNavigationRestore = null;
    }

    private void RestoreProcessNavigation(ProcessNavigationState state)
    {
        CancelNavigationRestore(); pendingNavigationRestore = state;
        ResumeProcessNavigation();
    }

    private bool NavigationModelMatches(ProcessNavigationState state) => model.Mode == state.Mode
        && model.ProcessQuery == state.Query && model.SortColumn == state.SortColumn && model.SortDescending == state.Descending;

    private bool NavigationPageVisible(ProcessNavigationState state) => processPage.Visibility == Visibility.Visible
        && view == (state.Mode == ProcessMode.Applications ? DesktopView.Applications : DesktopView.Processes);

    private void ResumeProcessNavigation()
    {
        if (pendingNavigationRestore is not { } state) return;
        if (applicationsClosed || !NavigationModelMatches(state)) { CancelNavigationRestore(); return; }
        if (!NavigationPageVisible(state)) {
            if (navigationRestoreFrame is not null) CompositionTarget.Rendering -= navigationRestoreFrame;
            navigationRestoreFrame = null; return;
        }
        if (navigationRestoreFrame is not null) return;
        processList.SelectedItems.Clear();
        var current = model.VisibleProcesses.Where(row => model.IsCurrent(row) && HasNavigationIdentity(row)).ToDictionary(row => row.Key);
        if (state.Primary is { } primary && current.TryGetValue(primary, out var selected)) SelectProcess(selected, false);
        foreach (var key in state.Selection) if (key != state.Primary && current.TryGetValue(key, out var row)
            && slotsByRow.TryGetValue(row, out var slot)) processList.SelectedItems.Add(slot);
        var restoredPrimary = SelectedProcess?.Key;
        var restoredSelection = processList.SelectedItems.OfType<ProcessSlot>().Select(slot => slot.Row.Key).ToHashSet();
        var restoredCollapsed = model.NavigationCollapsed.ToHashSet();
        int frames = 0, settled = 0;
        void Frame(object? sender, object args)
        {
            if (applicationsClosed || !NavigationModelMatches(state) || SelectedProcess?.Key != restoredPrimary
                || !restoredSelection.SetEquals(processList.SelectedItems.OfType<ProcessSlot>().Select(slot => slot.Row.Key))
                || !restoredCollapsed.SetEquals(model.NavigationCollapsed)) { CancelNavigationRestore(); return; }
            if (!NavigationPageVisible(state)) {
                CompositionTarget.Rendering -= navigationRestoreFrame; navigationRestoreFrame = null; return;
            }
            if (++frames > 8) { CancelNavigationRestore(); return; }
            if (Descendant<ScrollViewer>(processList) is not { } scroll) return;
            var horizontal = processTable.HorizontalScroll;
            double horizontalOffset = Math.Min(state.HorizontalOffset, horizontal.ScrollableWidth);
            bool aligned = false;
            if (state.Anchor is { } key && current.TryGetValue(key, out var row) && model.IsCurrent(row) && slotsByRow.TryGetValue(row, out var slot)) {
                if (frames == 1) { processList.ScrollIntoView(slot, ScrollIntoViewAlignment.Leading); processList.UpdateLayout(); }
                if (processList.ContainerFromItem(slot) is FrameworkElement { IsLoaded: true, ActualHeight: > 0 } container
                    && ReferenceEquals(processList.ItemFromContainer(container), slot)) {
                    double top = container.TransformToVisual(processList).TransformPoint(default).Y;
                    double offset = Math.Clamp(scroll.VerticalOffset + top - state.AnchorTop, 0, scroll.ScrollableHeight);
                    aligned = Math.Abs(top - state.AnchorTop) < .5;
                    if (!aligned) scroll.ChangeView(null, offset, null, true);
                }
            } else {
                double offset = Math.Min(state.VerticalOffset, scroll.ScrollableHeight);
                aligned = Math.Abs(scroll.VerticalOffset - offset) < .5;
                if (!aligned) scroll.ChangeView(null, offset, null, true);
            }
            if (Math.Abs(horizontal.HorizontalOffset - horizontalOffset) >= .5) { horizontal.ChangeView(horizontalOffset, null, null, true); aligned = false; }
            aligned &= RestoreNavigationGroupOffset(state, frames == 1);
            settled = aligned ? settled + 1 : 0;
            if (settled == 2) CancelNavigationRestore();
        }
        navigationRestoreFrame = Frame; CompositionTarget.Rendering += Frame;
    }

    private bool RestoreNavigationGroupOffset(ProcessNavigationState state, bool first)
    {
        if (state.Mode != ProcessMode.Applications || applicationWorkspace.ColumnDefinitions[0].Width.Value == 0
            || Descendant<ScrollViewer>(applicationGroups) is not { } scroll) return true;
        double offset = Math.Min(state.GroupOffset, scroll.ScrollableHeight);
        if (state.GroupAnchor is { } key && model.Applications.Visible.FirstOrDefault(row => row.Key == key) is { } row) {
            if (first) { applicationGroups.ScrollIntoView(row, ScrollIntoViewAlignment.Leading); applicationGroups.UpdateLayout(); }
            if (applicationGroups.ContainerFromItem(row) is not FrameworkElement { IsLoaded: true, ActualHeight: > 0 } container
                || !ReferenceEquals(applicationGroups.ItemFromContainer(container), row)) return false;
            double top = container.TransformToVisual(applicationGroups).TransformPoint(default).Y;
            offset = Math.Clamp(scroll.VerticalOffset + top - state.GroupTop, 0, scroll.ScrollableHeight);
            if (Math.Abs(top - state.GroupTop) < .5) return true;
        } else if (Math.Abs(scroll.VerticalOffset - offset) < .5) return true;
        scroll.ChangeView(null, offset, null, true); return false;
    }

    private void RefreshApplicationGroups()
    {
        bool apps = model.Mode == ProcessMode.Applications;
        if (!apps) { SetVisible(applicationHeader, false); ResizeApplicationGroups(); return; }
        updatingApplicationChoice = true;
        try { applicationGroups.SelectedItem = applicationChoice.SelectedItem = model.Applications.Selected; }
        finally { updatingApplicationChoice = false; }
        applicationStatus.Text = !model.Applications.HasInventory ? model.Applications.Error.Length > 0 ? Loc.T("Přehled není dostupný") : Loc.T("Načítám aplikace…")
            : Loc.F("{0} skupin · ", model.Applications.Visible.Count) + (model.Applications.Stale ? Loc.T("zastaralý přehled")
                : model.Applications.Partial ? Loc.T("částečný přehled") : Loc.T("podle oken"));
        string inventoryDetail = ApplicationInventoryDetail();
        ToolTipService.SetToolTip(applicationStatus, inventoryDetail); ToolTipService.SetToolTip(applicationChoice, inventoryDetail);
        if (model.Held) { model.Applications.RefreshMetrics(true); applicationStatus.Text += Loc.T(" · pořadí a skupiny přidrženy"); }
        string sortCaption = ApplicationSortTitle(model.Applications.Sort) + (model.Applications.SortDescending ? " ↓" : " ↑");
        if (applicationSort.Content as string != sortCaption) applicationSort.Content = sortCaption;
        // One icon; colour instead of "*" or "!" tells that the inventory is partial, held or unavailable.
        bool warning = model.Applications.Error.Length > 0 || (model.Applications.HasInventory && (model.Applications.Partial || model.Held));
        if (applicationInfo.Content as string != "ⓘ") applicationInfo.Content = "ⓘ";
        applicationInfo.Foreground = Display.Brush(warning ? 0xe0b04au : 0x95a4adu);
        ToolTipService.SetToolTip(applicationInfo, applicationStatus.Text + "\n" + inventoryDetail);
        if (applicationInfoOpen) applicationFacts.Text = ApplicationFacts();
        if (model.Applications.Selected is { } group) {
            if (applicationTitle.Text != group.Title) applicationTitle.Text = group.Title;
            if (applicationSummary.Text != group.Summary) applicationSummary.Text = group.Summary;
            ToolTipService.SetToolTip(applicationTitle, group.TitleTooltip);
        }
        SetVisible(applicationHeader, model.Applications.Selected is not null);
        // A group chosen automatically (first start, restored view) gets its root process selected too, once per group.
        if (model.Applications.Selected is { } chosen && chosen.Key != autoSelectedApplication)
            DispatcherQueue.TryEnqueue(() => { if (model.Mode == ProcessMode.Applications && model.Applications.Selected == chosen) SelectApplicationRoot(chosen); });
        ResizeApplicationGroups();
        RefreshAppTable();
    }

    /// <summary>Selects the group's root process so the process commands apply to the chosen application at once.</summary>
    private void SelectApplicationRoot(ApplicationGroupRow group)
    {
        autoSelectedApplication = group.Key;
        if (SelectedProcess is { } current && model.VisibleProcesses.Contains(current)) return;
        var root = model.VisibleProcesses.FirstOrDefault(row => row.Key == group.Key) ?? model.VisibleProcesses.FirstOrDefault();
        if (root is not null) SelectProcess(root, false);
    }

    private void RefreshApplicationInventory()
    {
        if (view == DesktopView.Applications && model.Mode == ProcessMode.Applications && !model.Paused && applicationRequest is null
            && NativeMonitor.MonotonicNowNs >= nextApplicationRead && model.BootIdentity is { State: SampleState.Valid } boot && !boot.Identifier.IsEmpty) {
            var kind = model.Processes.Any(row => row.Key.Kind == GenerationKind.SequenceNumber) ? GenerationKind.SequenceNumber : GenerationKind.CreationTime;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.Token); applicationRequest = cancellation;
            nextApplicationRead = NativeMonitor.MonotonicNowNs + 3_000_000_000;
            _ = ReadApplicationsAsync(boot.Identifier, kind, cancellation);
        }
    }

    private async Task ReadApplicationsAsync(BootIdentifier boot, GenerationKind kind, CancellationTokenSource request)
    {
        try {
            var snapshot = await processDetailsClient.ReadApplicationsAsync(boot, kind, request.Token);
            if (!applicationsClosed && !request.IsCancellationRequested && model.Mode == ProcessMode.Applications && model.BootIdentity.Identifier == boot) {
                model.ApplyApplications(snapshot);
                if (pendingApplicationSelection is { } key && model.Applications.Containing(key) is { } group && model.Applications.Visible.Contains(group)) {
                    model.SelectApplication(group.Key);
                    if (model.VisibleProcesses.FirstOrDefault(row => row.Key == key) is { } row) SelectProcess(row, false);
                    pendingApplicationSelection = null;
                }
            }
        } catch (OperationCanceledException) when (request.IsCancellationRequested || applicationsClosed) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or ObjectDisposedException) {
            if (!applicationsClosed && !request.IsCancellationRequested) model.Applications.Failed(error.Message);
        } finally {
            applicationRequest = null; request.Dispose();
            nextApplicationRead = Math.Max(nextApplicationRead, NativeMonitor.MonotonicNowNs + 3_000_000_000);
            if (applicationsClosed) applicationLifetime.Dispose(); else RefreshProcesses();
        }
    }

    private string ApplicationInventoryDetail() => model.Applications.Error.Length > 0 ? model.Applications.Error
        : model.Applications.Partial ? Loc.T("Některá okna, vazby procesů nebo údaje nemusí být dostupné. ≥ u CPU označuje částečný součet.")
            : Loc.T("Skupiny vycházejí z ověřených vlastníků viditelných oken a jejich potomků.");

    private string ApplicationFacts()
    {
        if (model.Applications.Selected is not { } group) return ApplicationInventoryDetail();
        var first = group.Windows[0];
        var text = new StringBuilder(group.TitleTooltip).Append(" · ").Append(group.Summary)
            .Append(Loc.F("\nKořen PID {0} · {1} · generace {2}", group.Key.Pid, group.Key.Kind, group.Key.Generation));
        if (first.AppIdState == SampleState.Valid) text.Append("\nAUMID: ").Append(first.AppId);
        if (first.PackageState == SampleState.Valid) text.Append(Loc.T(" · balíček: ")).Append(first.Package);
        var hosts = group.Windows.Where(window => window.Host.HasValue).Select(window => window.Host!.Value.Pid).Distinct().ToArray();
        if (hosts.Length > 0) text.Append(Loc.F("\nHostitel pozorovaný v inventáři: PID {0}; sdílený host se nezapočítává více aplikacím.", string.Join(", ", hosts)));
        text.Append(Loc.T("\nCPU je součet dostupných validních hodnot.\n\n")).Append(ApplicationInventoryDetail());
        return text.ToString();
    }

    private void ResizeApplicationGroups()
    {
        if (applicationWorkspace.ColumnDefinitions.Count == 0) return;
        bool apps = model.Mode == ProcessMode.Applications;
        var column = applicationWorkspace.ColumnDefinitions[0];
        // Applications use the single grouped table (F133); the old group list, its sort button and the process table stay for the Processes view only.
        if (column.Width.GridUnitType != GridUnitType.Pixel || column.Width.Value != 0) column.Width = new GridLength(0);
        SetVisible(applicationWorkspace.Children[0], false); SetVisible(applicationWorkspace.Children[1], !apps); SetVisible(appsPane, apps);
        SetVisible(applicationToolbar, apps); SetVisible(applicationChoice, false); SetVisible(applicationSort, false);
    }

    private static void SetVisible(UIElement element, bool visible)
    {
        var value = visible ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != value) element.Visibility = value;
    }

    private static (ApplicationGroupRow Row, int Index, double Top)? FirstVisibleApplicationGroup(ListView list)
    {
        if (!list.IsLoaded || !double.IsFinite(list.ActualWidth) || !double.IsFinite(list.ActualHeight)
            || list.ActualWidth <= 0 || list.ActualHeight <= 0 || list.ItemsPanelRoot is not { } panel) return null;
        (ApplicationGroupRow Row, int Index, double Top)? first = null;
        double highest = double.PositiveInfinity;
        foreach (var child in panel.Children) {
            if (child is not FrameworkElement { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } container) continue;
            int index = list.IndexFromContainer(container);
            if (index < 0 || index >= list.Items.Count || list.ItemFromContainer(container) is not ApplicationGroupRow row
                || !ReferenceEquals(list.Items[index], row)) continue;
            var transform = container.TransformToVisual(list);
            var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom)
                || bounds.Width <= 0 || bounds.Height <= 0 || bounds.Right <= 0 || bounds.Bottom <= 0
                || bounds.Left >= list.ActualWidth || bounds.Top >= list.ActualHeight) continue;
            bool visible = true;
            for (DependencyObject? ancestor = container; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is UIElement { Visibility: Visibility.Collapsed }) { visible = false; break; }
            if (!visible || bounds.Top > highest || bounds.Top == highest && first is { } previous && index >= previous.Index) continue;
            double top = transform.TransformPoint(default).Y;
            if (!double.IsFinite(top)) continue;
            highest = bounds.Top; first = (row, index, top);
        }
        return first;
    }

    internal static Action PreserveApplicationGroupView(ApplicationGroupModel source, ListView list, ComboBox choice)
    {
        ApplicationGroupRow? selection = null, anchor = null;
        ScrollViewer? scroll = null;
        double offset = 0, anchorTop = 0;
        bool bound = false;
        void Changing()
        {
            bound = ReferenceEquals(list.ItemsSource, source.Visible);
            if (!bound) return;
            selection = list.SelectedItem as ApplicationGroupRow; anchor = null;
            if (list.Visibility != Visibility.Visible) return;
            scroll ??= Descendant<ScrollViewer>(list); offset = scroll?.VerticalOffset ?? 0;
            var visibleAnchor = FirstVisibleApplicationGroup(list);
            int first = visibleAnchor?.Index ?? -1;
            if (visibleAnchor is { } captured) {
                anchor = captured.Row; anchorTop = captured.Top;
            }
            if (App.ApplicationGroupsCheckPath is not null) {
                var panel = list.ItemsPanelRoot; string nativePanel = "<null>";
                if (panel is not null) {
                    try { nativePanel = new WinRT.IInspectable(((WinRT.IWinRTObject)panel).NativeObject).GetRuntimeClassName(false); }
                    catch (Exception error) { nativePanel = $"error:{error.GetType().Name}:{error.HResult:X8}"; }
                }
                App.Trace(FormattableString.Invariant($"application-view changing;bound={bound};updating={source.Updating};selection={selection?.Key};modelSelection={source.Selected?.Key};listModelRef={ReferenceEquals(list.SelectedItem, source.Selected)};choiceModelRef={ReferenceEquals(choice.SelectedItem, source.Selected)};scrollNull={scroll is null};offset={offset};scrollable={scroll?.ScrollableHeight};viewport={scroll?.ViewportHeight};extent={scroll?.ExtentHeight};panelType={panel?.GetType().FullName};panelNative={nativePanel};panelCast={panel is ItemsStackPanel};firstIndex={first};anchor={anchor?.Key};anchorTop={anchorTop};listWidth={list.ActualWidth};listHeight={list.ActualHeight}"));
            }
        }
        void Changed()
        {
            if (!bound) return;
            var selected = selection is not null && source.Visible.Contains(selection) ? selection : source.Selected ?? source.Visible.FirstOrDefault();
            list.SelectedItem = selected;
            if (ReferenceEquals(choice.ItemsSource, source.Visible)) choice.SelectedItem = selected;
            double? requestedOffset = null, currentAnchorTop = null; bool? changeAccepted = null;
            if (scroll is not null && list.Visibility == Visibility.Visible) {
                if (anchor is not null && source.Visible.Contains(anchor)) {
                    list.ScrollIntoView(anchor, ScrollIntoViewAlignment.Leading); list.UpdateLayout();
                    if (list.ContainerFromItem(anchor) is FrameworkElement container) {
                        currentAnchorTop = container.TransformToVisual(list).TransformPoint(default).Y;
                        requestedOffset = Math.Clamp(scroll.VerticalOffset + currentAnchorTop.Value - anchorTop, 0, scroll.ScrollableHeight);
                        changeAccepted = scroll.ChangeView(null, requestedOffset.Value, null, true);
                    }
                } else {
                    requestedOffset = Math.Min(offset, scroll.ScrollableHeight);
                    changeAccepted = scroll.ChangeView(null, requestedOffset.Value, null, true);
                }
            }
            if (App.ApplicationGroupsCheckPath is not null)
                App.Trace(FormattableString.Invariant($"application-view changed;bound={bound};updating={source.Updating};selection={selected?.Key};modelSelection={source.Selected?.Key};listSelectionRef={ReferenceEquals(list.SelectedItem, selected)};choiceSelectionRef={ReferenceEquals(choice.SelectedItem, selected)};listModelRef={ReferenceEquals(list.SelectedItem, source.Selected)};choiceModelRef={ReferenceEquals(choice.SelectedItem, source.Selected)};scrollNull={scroll is null};visibility={list.Visibility};offset={scroll?.VerticalOffset};scrollable={scroll?.ScrollableHeight};anchor={anchor?.Key};anchorTop={anchorTop};currentAnchorTop={currentAnchorTop};requestedOffset={requestedOffset};changeAccepted={changeAccepted}"));
        }
        source.ViewChanging += Changing; source.ViewChanged += Changed;
        return () => { source.ViewChanging -= Changing; source.ViewChanged -= Changed; };
    }
}
