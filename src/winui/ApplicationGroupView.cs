using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Grid applicationWorkspace = new() { ColumnSpacing = 10, RowSpacing = 6 };
    private readonly ListView applicationGroups = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0) };
    private readonly ApplicationGroupTemplates applicationTemplates = new();
    private readonly ComboBox applicationChoice = new() { DisplayMemberPath = nameof(ApplicationGroupRow.Title), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
    private readonly Grid applicationToolbar = new() { ColumnSpacing = 6 };
    private readonly ComboBox applicationSort = new() { MinHeight = 30, Width = 145, FontSize = 11 };
    private readonly Button applicationSortDirection = Button("↑");
    private readonly TextBlock applicationStatus = Label("", 11, 0x95a4ad), applicationFacts = Label("", 11, 0x95a4ad);
    private readonly Expander applicationInfo = new() { Header = "Informace o skupině", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly CancellationTokenSource applicationLifetime = new();
    private CancellationTokenSource? applicationRequest;
    private bool updatingApplicationChoice, applicationsClosed;
    private long nextApplicationRead;
    private ProcessKey? pendingApplicationSelection, applicationReturnSelection, applicationReturnAnchor;
    private ProcessMode applicationReturnMode;
    private double applicationReturnOffset;

    private FrameworkElement BuildApplicationGroups(FrameworkElement table)
    {
        applicationWorkspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) }); applicationWorkspace.ColumnDefinitions.Add(new ColumnDefinition());
        applicationWorkspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); applicationWorkspace.RowDefinitions.Add(new RowDefinition());
        applicationWorkspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sidebar = new Grid { RowSpacing = 6 };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); sidebar.RowDefinitions.Add(new RowDefinition());
        applicationStatus.TextWrapping = applicationFacts.TextWrapping = TextWrapping.Wrap;
        applicationStatus.MaxLines = 2; applicationStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        var note = new ScrollViewer { Content = applicationStatus, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        sidebar.Children.Add(note); sidebar.Children.Add(applicationGroups); Grid.SetRow(applicationGroups, 1);
        applicationGroups.ItemsSource = applicationChoice.ItemsSource = model.Applications.Visible;
        applicationGroups.ItemTemplate = (DataTemplate)applicationTemplates["ApplicationGroup"];
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        applicationGroups.ItemContainerStyle = style;
        applicationWorkspace.Children.Add(sidebar); Grid.SetRow(sidebar, 1); applicationFacts.Visibility = Visibility.Collapsed;
        applicationToolbar.ColumnDefinitions.Add(new ColumnDefinition()); applicationToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        applicationToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        applicationToolbar.Children.Add(applicationChoice); applicationToolbar.Children.Add(applicationSort); Grid.SetColumn(applicationSort, 1);
        applicationSortDirection.Width = 32; applicationSortDirection.MinHeight = 30; applicationSortDirection.Padding = new Thickness(0);
        applicationToolbar.Children.Add(applicationSortDirection); Grid.SetColumn(applicationSortDirection, 2);
        applicationWorkspace.Children.Add(applicationToolbar); Grid.SetColumnSpan(applicationToolbar, 2);
        foreach (string option in new[] { "Název", "CPU", "Paměť", "Počet procesů" }) applicationSort.Items.Add(option);
        applicationSort.SelectedIndex = (int)model.Applications.Sort;
        void Sort(ApplicationGroupSort value, bool descending) {
            model.Applications.SetSort(value, descending); applicationSortDirection.Content = descending ? "↓" : "↑";
            AutomationProperties.SetName(applicationSortDirection, descending ? "Řazení sestupně; změnit směr" : "Řazení vzestupně; změnit směr");
            ToolTipService.SetToolTip(applicationSortDirection, descending ? "Sestupně" : "Vzestupně"); RefreshApplicationGroups();
        }
        applicationSort.SelectionChanged += (_, _) => { if (applicationSort.SelectedIndex >= 0) {
            var value = (ApplicationGroupSort)applicationSort.SelectedIndex; Sort(value, value != ApplicationGroupSort.Name);
        } };
        applicationSortDirection.Click += (_, _) => Sort(model.Applications.Sort, !model.Applications.SortDescending);
        AutomationProperties.SetName(applicationSort, "Řadit skupiny aplikací podle"); AutomationProperties.SetAutomationId(applicationSort, "application-sort");
        AutomationProperties.SetName(applicationSortDirection, "Řazení vzestupně; změnit směr"); AutomationProperties.SetAutomationId(applicationSortDirection, "application-sort-direction");
        ToolTipService.SetToolTip(applicationSort, "Řazení skupin aplikací; paměť je součet pracovních sad dostupných procesů.");
        applicationWorkspace.Children.Add(table); Grid.SetColumn(table, 1); Grid.SetRow(table, 1);
        applicationInfo.Content = new ScrollViewer { Content = applicationFacts, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(12, 6, 12, 8) };
        applicationWorkspace.Children.Add(applicationInfo); Grid.SetRow(applicationInfo, 2); Grid.SetColumnSpan(applicationInfo, 2);
        void Select(object? value) {
            if (updatingApplicationChoice || model.Applications.Updating || model.Mode != ProcessMode.Applications || value is not ApplicationGroupRow group) return;
            if (!model.Processes.Any(row => row.Key == group.Key && model.IsCurrent(row))) return;
            model.SelectApplication(group.Key); pendingApplicationSelection = null; RefreshProcesses();
        }
        applicationGroups.SelectionChanged += (_, _) => Select(applicationGroups.SelectedItem);
        applicationChoice.SelectionChanged += (_, _) => Select(applicationChoice.SelectedItem);
        AutomationProperties.SetName(applicationGroups, "Skupiny aplikací podle ověřených oken"); AutomationProperties.SetAutomationId(applicationGroups, "application-groups");
        AutomationProperties.SetName(applicationChoice, "Vybrat skupinu aplikace"); AutomationProperties.SetAutomationId(applicationChoice, "application-choice");
        var releaseView = PreserveApplicationGroupView(model.Applications, applicationGroups, applicationChoice);
        Closed += (_, _) => { releaseView(); applicationsClosed = true; applicationRequest?.Cancel(); applicationLifetime.Cancel(); if (applicationRequest is null) applicationLifetime.Dispose(); };
        ResizeApplicationGroups();
        return applicationWorkspace;
    }

    private void SelectProcessMode(ProcessMode mode)
    {
        if (mode == ProcessMode.Applications && model.Mode != mode) {
            applicationReturnMode = model.Mode; pendingApplicationSelection = applicationReturnSelection = SelectedProcess?.Key;
            var scroll = Descendant<ScrollViewer>(processList);
            double offset = scroll?.VerticalOffset ?? 0; int index = (int)(offset / ProcessTable.RowHeight);
            applicationReturnAnchor = index < model.VisibleProcesses.Count ? model.VisibleProcesses[index].Key : null;
            applicationReturnOffset = offset % ProcessTable.RowHeight;
        }
        bool leaving = model.Mode == ProcessMode.Applications && mode != model.Mode;
        var selected = SelectedProcess?.Key ?? applicationReturnSelection;
        if (leaving) applicationRequest?.Cancel();
        model.SetMode(mode); RefreshProcesses();
        if (leaving) {
            if (selected is { } key && model.VisibleProcesses.FirstOrDefault(row => row.Key == key) is { } row) SelectProcess(row, false);
            if (mode == applicationReturnMode && applicationReturnAnchor is { } anchor) {
                int index = model.VisibleProcesses.ToList().FindIndex(row => row.Key == anchor);
                if (index >= 0) { processList.UpdateLayout(); Descendant<ScrollViewer>(processList)?.ChangeView(null, index * ProcessTable.RowHeight + applicationReturnOffset, null, true); }
            }
            pendingApplicationSelection = null;
        }
    }

    private void ShowApplications(bool visible)
    {
        if (visible) SelectProcessMode(ProcessMode.Applications);
        else if (model.Mode == ProcessMode.Applications) SelectProcessMode(applicationReturnMode);
    }

    private void RefreshApplicationGroups()
    {
        bool apps = model.Mode == ProcessMode.Applications;
        applicationFacts.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        applicationInfo.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        if (!apps) { ResizeApplicationGroups(); return; }
        updatingApplicationChoice = true;
        try { applicationGroups.SelectedItem = applicationChoice.SelectedItem = model.Applications.Selected; }
        finally { updatingApplicationChoice = false; }
        applicationStatus.Text = !model.Applications.HasInventory ? model.Applications.Error.Length > 0 ? "Přehled není dostupný" : "Načítám aplikace…"
            : model.Applications.Visible.Count + " skupin · " + (model.Applications.Stale ? "zastaralý přehled"
                : model.Applications.Partial ? "částečný přehled" : "podle oken");
        string inventoryDetail = model.Applications.Error.Length > 0 ? model.Applications.Error
            : model.Applications.Partial ? "Některá okna, vazby procesů nebo údaje nemusí být dostupné. * u CPU označuje částečný součet."
                : "Skupiny vycházejí z ověřených vlastníků viditelných oken a jejich potomků.";
        ToolTipService.SetToolTip(applicationStatus, inventoryDetail); ToolTipService.SetToolTip(applicationChoice, inventoryDetail);
        if (model.Held) { model.Applications.RefreshMetrics(true); applicationStatus.Text += " · pořadí a skupiny přidrženy"; }
        applicationFacts.Text = ApplicationFacts();
        ResizeApplicationGroups();
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

    private string ApplicationFacts()
    {
        if (model.Applications.Selected is not { } group) return "Vyberte skupinu aplikace. Skupiny vycházejí z ověřených vlastníků oken a vazeb potomků podle času vzniku.";
        var first = group.Windows[0];
        var text = new StringBuilder(group.Title).Append(" · ").Append(group.Summary)
            .Append("\nKořen PID ").Append(group.Key.Pid).Append(" · ").Append(group.Key.Kind).Append(" · generace ").Append(group.Key.Generation);
        if (first.AppIdState == SampleState.Valid) text.Append("\nAUMID: ").Append(first.AppId);
        if (first.PackageState == SampleState.Valid) text.Append(" · balíček: ").Append(first.Package);
        var hosts = group.Windows.Where(window => window.Host.HasValue).Select(window => window.Host!.Value.Pid).Distinct().ToArray();
        if (hosts.Length > 0) text.Append("\nHostitel pozorovaný v inventáři: PID ").AppendJoin(", ", hosts).Append("; sdílený host se nezapočítává více aplikacím.");
        text.Append("\nCPU je součet dostupných validních hodnot.");
        return text.ToString();
    }

    private void ResizeApplicationGroups()
    {
        if (applicationWorkspace.ColumnDefinitions.Count == 0) return;
        bool apps = model.Mode == ProcessMode.Applications, wide = root.ActualWidth >= 1200;
        applicationWorkspace.ColumnDefinitions[0].Width = new GridLength(apps && wide ? 250 : 0);
        var sidebar = applicationWorkspace.Children[0]; sidebar.Visibility = apps && wide ? Visibility.Visible : Visibility.Collapsed;
        applicationToolbar.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        applicationChoice.Visibility = apps && !wide ? Visibility.Visible : Visibility.Collapsed;
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
