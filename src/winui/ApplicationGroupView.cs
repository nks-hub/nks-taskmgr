using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Grid applicationWorkspace = new() { ColumnSpacing = 10, RowSpacing = 6 };
    private readonly ListView applicationGroups = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0) };
    private readonly ApplicationGroupTemplates applicationTemplates = new();
    private readonly ComboBox applicationChoice = new() { DisplayMemberPath = nameof(ApplicationGroupRow.Title), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
    private readonly TextBlock applicationStatus = Label("", 11, 0x95a4ad), applicationFacts = Label("", 11, 0x95a4ad);
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
        var sidebar = new Grid { RowSpacing = 6 };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); sidebar.RowDefinitions.Add(new RowDefinition());
        applicationStatus.TextWrapping = applicationFacts.TextWrapping = TextWrapping.Wrap;
        var note = new ScrollViewer { Content = applicationStatus, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        sidebar.Children.Add(note); sidebar.Children.Add(applicationGroups); Grid.SetRow(applicationGroups, 1);
        applicationGroups.ItemsSource = applicationChoice.ItemsSource = model.Applications.Visible;
        applicationGroups.ItemTemplate = (DataTemplate)applicationTemplates["ApplicationGroup"];
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        applicationGroups.ItemContainerStyle = style;
        applicationWorkspace.Children.Add(sidebar); Grid.SetRow(sidebar, 1); applicationFacts.Visibility = Visibility.Collapsed;
        applicationWorkspace.Children.Add(applicationChoice); Grid.SetColumnSpan(applicationChoice, 2);
        applicationWorkspace.Children.Add(table); Grid.SetColumn(table, 1); Grid.SetRow(table, 1);
        void Select(object? value) {
            if (updatingApplicationChoice || model.Applications.Updating || model.Mode != ProcessMode.Applications || value is not ApplicationGroupRow group) return;
            if (!model.Processes.Any(row => row.Key == group.Key && model.IsCurrent(row))) return;
            model.SelectApplication(group.Key); pendingApplicationSelection = null; RefreshProcesses();
        }
        applicationGroups.SelectionChanged += (_, _) => Select(applicationGroups.SelectedItem);
        applicationChoice.SelectionChanged += (_, _) => Select(applicationChoice.SelectedItem);
        AutomationProperties.SetName(applicationGroups, "Skupiny aplikací podle ověřených oken"); AutomationProperties.SetAutomationId(applicationGroups, "application-groups");
        AutomationProperties.SetName(applicationChoice, "Vybrat skupinu aplikace"); AutomationProperties.SetAutomationId(applicationChoice, "application-choice");
        Closed += (_, _) => { applicationsClosed = true; applicationRequest?.Cancel(); applicationLifetime.Cancel(); if (applicationRequest is null) applicationLifetime.Dispose(); };
        ResizeApplicationGroups();
        return applicationWorkspace;
    }

    private void SelectProcessMode(ProcessMode mode)
    {
        if (mode == ProcessMode.Applications && model.Mode != mode) {
            applicationReturnMode = model.Mode; pendingApplicationSelection = applicationReturnSelection = SelectedProcess?.Key;
            var scroll = Descendant<ScrollViewer>(processList);
            double offset = scroll?.VerticalOffset ?? 0; int index = (int)(offset / 34);
            applicationReturnAnchor = index < model.VisibleProcesses.Count ? model.VisibleProcesses[index].Key : null;
            applicationReturnOffset = offset % 34;
        }
        bool leaving = model.Mode == ProcessMode.Applications && mode != model.Mode;
        var selected = SelectedProcess?.Key ?? applicationReturnSelection;
        if (leaving) applicationRequest?.Cancel();
        model.SetMode(mode); RefreshProcesses();
        if (leaving) {
            if (selected is { } key && model.VisibleProcesses.FirstOrDefault(row => row.Key == key) is { } row) SelectProcess(row, false);
            if (mode == applicationReturnMode && applicationReturnAnchor is { } anchor) {
                int index = model.VisibleProcesses.ToList().FindIndex(row => row.Key == anchor);
                if (index >= 0) { processList.UpdateLayout(); Descendant<ScrollViewer>(processList)?.ChangeView(null, index * 34 + applicationReturnOffset, null, true); }
            }
            pendingApplicationSelection = null;
        }
    }

    private void RefreshApplicationGroups()
    {
        bool apps = model.Mode == ProcessMode.Applications;
        applicationFacts.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        if (!apps) { ResizeApplicationGroups(); return; }
        updatingApplicationChoice = true;
        try { applicationGroups.SelectedItem = applicationChoice.SelectedItem = model.Applications.Selected; }
        finally { updatingApplicationChoice = false; }
        applicationStatus.Text = model.Applications.Error.Length > 0 ? model.Applications.Error
            : !model.Applications.HasInventory ? "Načítám ověřené vlastníky oken…" : model.Applications.Visible.Count + " skupin aplikací"
                + (model.Applications.Partial ? " · neúplný nebo zastaralý inventář" : " · podle viditelných oken");
        if (model.Held) { model.Applications.RefreshMetrics(true); applicationStatus.Text += " · pořadí a skupiny přidrženy"; }
        applicationFacts.Text = applicationStatus.Text + "\n" + ApplicationFacts();
        ResizeApplicationGroups();
        if (view == DesktopView.Processes && !IsIconic(windowHandle) && !model.Paused && applicationRequest is null
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
        if (model.Applications.Partial) text.Append(" Inventář je částečný nebo zastaralý.");
        return text.ToString();
    }

    private void ResizeApplicationGroups()
    {
        if (applicationWorkspace.ColumnDefinitions.Count == 0) return;
        bool apps = model.Mode == ProcessMode.Applications, wide = root.ActualWidth >= 1200;
        applicationWorkspace.ColumnDefinitions[0].Width = new GridLength(apps && wide ? 250 : 0);
        var sidebar = applicationWorkspace.Children[0]; sidebar.Visibility = apps && wide ? Visibility.Visible : Visibility.Collapsed;
        applicationChoice.Visibility = apps && !wide ? Visibility.Visible : Visibility.Collapsed;
        if (processPage.RowDefinitions.Count > 3) processPage.RowDefinitions[3].Height = new GridLength(apps ? root.ActualHeight < 650 ? 120 : 160 : 92);
    }
}
