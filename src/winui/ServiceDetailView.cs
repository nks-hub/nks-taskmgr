using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using System.Collections.ObjectModel;
using Windows.System;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly TextBlock serviceTitle = DetailLabel(Loc.T("Vyberte službu"), 16), serviceName = DetailLabel("", 10), serviceDescription = DetailLabel("", 11);
    private readonly TextBlock serviceState = DetailLabel("", 11), serviceStartup = DetailLabel("", 11), serviceAccount = DetailLabel("", 11);
    private readonly TextBlock serviceGroup = DetailLabel("", 11), serviceCommand = DetailLabel("", 11), serviceDetailNote = DetailLabel("", 10);
    private readonly TextBlock serviceReportedHost = DetailLabel("", 11), serviceHostIdentityNote = DetailLabel("", 10);
    private readonly StackPanel serviceSharedHost = new() { Spacing = 8 };
    private readonly ObservableCollection<ServiceRow> serviceHostPeers = [];
    private readonly ListView serviceHostPeerList = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
        MaxHeight = 220, Padding = new Thickness(0), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button serviceHost = Button(Loc.T("Hostitelský proces") + " →"), serviceOverviewTab = Button(Loc.T("Přehled")), serviceDependenciesTab = Button(Loc.T("Závislosti"));
    private readonly StackPanel serviceOverview = new() { Spacing = 16 }, serviceDependencies = new() { Spacing = 8 };
    private readonly StackPanel serviceTabs = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
    private readonly List<(Button Button, string Name)> dependencyButtons = [];
    private string dependencySelection = "";
    private string[] shownDependencies = [], shownDependents = [];
    private uint shownDependencyError = uint.MaxValue, shownDependentError = uint.MaxValue;
    private bool dependenciesSelected;

    private static TextBlock DetailLabel(string value, double size) => new() {
        Text = value, FontSize = Math.Max(12, size), FontFamily = UiTypography.Font, Foreground = Display.Brush(size < 12 ? 0x95a4adu : 0xe9eef0u),
        FontWeight = size >= 14 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
    };
    private void BuildServiceDetail()
    {
        serviceDetailScroll.Background = Display.Brush(0x171d21); serviceDetailScroll.BorderBrush = Display.Brush(0x2c363d);
        var content = new StackPanel { Spacing = 12, Padding = new Thickness(16, 12, 20, 12) };
        var emblem = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(4), Background = Display.Brush(0x1d252a), HorizontalAlignment = HorizontalAlignment.Left, Child = UiIcon.Create("services", 0xedc08b, 18) };
        content.Children.Add(emblem); content.Children.Add(serviceTitle); content.Children.Add(serviceName); content.Children.Add(serviceDescription);
        serviceName.FontFamily = new FontFamily("Consolas");
        serviceHost.HorizontalAlignment = HorizontalAlignment.Left;
        serviceHost.Click += (_, _) => OpenServiceHost();
        content.Children.Add(serviceHost); content.Children.Add(serviceHostIdentityNote);
        foreach (var button in new[] { serviceOverviewTab, serviceDependenciesTab }) { button.Height = 27; serviceTabs.Children.Add(button); }
        serviceOverviewTab.Click += (_, _) => { dependenciesSelected = false; RefreshServiceDetail(); };
        serviceDependenciesTab.Click += (_, _) => { dependenciesSelected = true; RefreshServiceDetail(); }; content.Children.Add(serviceTabs);
        void Fact(string label, TextBlock value) { var group = new StackPanel { Spacing = 6 }; group.Children.Add(DetailLabel(label, 9)); group.Children.Add(value); serviceOverview.Children.Add(group); }
        Fact(Loc.T("Stav služby"), serviceState); Fact(Loc.T("Hostitel podle správce služeb"), serviceReportedHost);
        Fact(Loc.T("Spouštění"), serviceStartup); Fact(Loc.T("Účet"), serviceAccount);
        serviceSharedHost.Children.Add(DetailLabel(Loc.T("Další služby se stejným hlášeným PID"), 11));
        serviceHostPeerList.ItemsSource = serviceHostPeers;
        serviceHostPeerList.ItemTemplate = serviceHostTemplates["ServiceHostPeerTemplate"] as DataTemplate;
        var peerStyle = new Style { TargetType = typeof(ListViewItem) };
        peerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        peerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        serviceHostPeerList.ItemContainerStyle = peerStyle;
        ScrollViewer.SetVerticalScrollBarVisibility(serviceHostPeerList, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(serviceHostPeerList, ScrollBarVisibility.Disabled);
        serviceHostPeerList.ItemClick += (_, args) => {
            if (args.ClickedItem is ServiceRow peer && servicesModel?.Selected is { Pid: > 0 } selected
                && peer.Pid == selected.Pid && ReferenceEquals(servicesModel.Find(peer.Name), peer)) SelectRelatedService(peer.Name);
        };
        AutomationProperties.SetName(serviceHostPeerList, Loc.T("Další služby se stejným hlášeným PID"));
        AutomationProperties.SetAutomationId(serviceHostPeerList, "services-host-peers");
        serviceSharedHost.Children.Add(serviceHostPeerList); serviceOverview.Children.Add(serviceSharedHost);
        Fact(Loc.T("Skupina"), serviceGroup); Fact(Loc.T("Příkaz"), serviceCommand);
        content.Children.Add(serviceOverview); content.Children.Add(serviceDependencies); content.Children.Add(serviceDetailNote); serviceDetailScroll.Content = content;
    }
    private void OpenServiceHost()
    {
        if (servicesModel?.Host(model) is not { } host) return;
        processQuery = ""; model.SetQuery(""); model.SetMode(ProcessMode.All); ShowView(false);
        SelectProcess(host);
    }
    private void RefreshServiceDetail()
    {
        var row = servicesModel?.Selected; var detail = servicesModel?.Detail;
        if (row is null) detail = null;
        ResizeServices();
        serviceTitle.Text = row?.Title ?? Loc.T("Vyberte službu"); serviceName.Text = row?.Name ?? "";
        serviceDescription.Text = row is null ? Loc.T("Výběr v seznamu otevře konfiguraci a závislosti služby.") : detail is null
            ? servicesModel!.DetailError.Length > 0 ? servicesModel.DetailError : model.Paused ? Loc.T("Obnovte sběr pro načtení detailu.") : Loc.T("Načítám detail…")
            : detail.DescriptionError != 0 ? ServiceText.Error(detail.DescriptionError) : detail.Description;
        var config = detail?.Config ?? row?.Config;
        serviceState.Text = row is null ? "—" : detail is not null && detail.StatusError != 0 ? ServiceText.Error(detail.StatusError) : ServiceText.State(detail?.State ?? row.State);
        serviceStartup.Text = config?.Startup ?? "—";
        serviceAccount.Text = config is null ? "—" : config.Error != 0 ? ServiceText.Error(config.Error) : config.Account;
        serviceGroup.Text = config is null ? "—" : config.Error != 0 ? ServiceText.Error(config.Error) : config.Group.Length == 0 ? Loc.T("Bez skupiny") : config.Group;
        serviceCommand.Text = config is null ? "—" : config.Error != 0 ? ServiceText.Error(config.Error) : config.Command;
        serviceHost.IsEnabled = servicesModel?.Host(model) is not null;
        serviceHost.Visibility = serviceTabs.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        string hostHint = serviceHost.IsEnabled ? Loc.T("Otevřít hostitelský proces.") : model.Paused ? Loc.T("Obnovte sběr pro otevření hostitele.")
            : detail?.HostError > 0 ? Loc.F("Hostitele nelze ověřit: {0}", ServiceText.Error(detail.HostError))
            : row?.State != 4 ? Loc.T("Služba není spuštěná.") : Loc.T("Hostitelský proces se změnil nebo data nejsou aktuální.");
        ToolTipService.SetToolTip(serviceHost, hostHint); AutomationProperties.SetHelpText(serviceHost, hostHint);
        serviceReportedHost.Text = row?.HostSummary ?? "—";
        serviceHostIdentityNote.Text = row is null ? "" : row.Pid == 0 ? Loc.T("Služba nehlásí žádný PID.")
            : (serviceHost.IsEnabled ? Loc.T("Živý odkaz ověřuje hostitele vybrané služby.") : Loc.T("Živý odkaz není ověřený.") + " " + hostHint)
                + " " + Loc.T("Seskupení služeb vychází pouze z hlášeného PID, ne z ověřené totožnosti procesu.");
        RefreshReportedHostServices(row);
        serviceOverview.Visibility = row is not null && !dependenciesSelected ? Visibility.Visible : Visibility.Collapsed;
        serviceDependencies.Visibility = row is not null && dependenciesSelected ? Visibility.Visible : Visibility.Collapsed;
        serviceOverviewTab.Background = Display.Brush(dependenciesSelected ? 0x1d252au : 0x28343bu);
        serviceDependenciesTab.Background = Display.Brush(dependenciesSelected ? 0x28343bu : 0x1d252au);
        serviceDetailNote.Text = row is null ? "" : servicesModel!.DetailError.Length > 0 ? servicesModel.DetailError : detail is null ? ""
            : Loc.F("Detail · stáří {0}", Display.Number((NksTaskMgr.Native.NativeMonitor.MonotonicNowNs - detail.Time) / 1_000_000_000d, " s"))
                + (servicesModel.Stale ? " · " + Loc.T("zastaralý seznam") : "");
        var dependencies = config?.Dependencies ?? []; var dependents = detail?.Dependents ?? [];
        uint dependencyError = config?.Error ?? 232, dependentError = detail?.DependentsError ?? 232;
        if (dependencySelection != (row?.Name ?? "") || dependencyError != shownDependencyError || dependentError != shownDependentError
            || !shownDependencies.SequenceEqual(dependencies) || !shownDependents.SequenceEqual(dependents)) {
            dependencySelection = row?.Name ?? ""; shownDependencies = dependencies; shownDependents = dependents;
            shownDependencyError = dependencyError; shownDependentError = dependentError; serviceDependencies.Children.Clear(); dependencyButtons.Clear();
            AddDependencies(Loc.T("Potřebuje ke spuštění"), dependencies, dependencyError); AddDependencies(Loc.T("Navazující služby"), dependents, dependentError);
        }
        foreach (var (button, name) in dependencyButtons) button.IsEnabled = !name.StartsWith('+') && servicesModel?.Find(name) is not null;
    }
    private void AddDependencies(string heading, string[] names, uint error)
    {
        serviceDependencies.Children.Add(DetailLabel(heading, 11));
        if (error != 0) { serviceDependencies.Children.Add(DetailLabel(ServiceText.Error(error), 10)); return; }
        if (names.Length == 0) { serviceDependencies.Children.Add(DetailLabel(Loc.T("Žádné"), 10)); return; }
        foreach (string name in names) {
            var button = Button(name.StartsWith('+') ? Loc.F("Skupina: {0}", name[1..]) : name + " →");
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetName(button, Loc.F("Závislost {0}", name));
            button.Click += (_, _) => {
                if (!name.StartsWith('+')) SelectRelatedService(name);
            };
            dependencyButtons.Add((button, name)); serviceDependencies.Children.Add(button);
        }
    }
    private void RefreshReportedHostServices(ServiceRow? row)
    {
        ServiceRow[] peers = row is null ? [] : servicesModel!.ReportedHostServices(row).Where(peer => !ReferenceEquals(peer, row))
            .OrderBy(peer => peer.Title, StringComparer.OrdinalIgnoreCase).ThenBy(peer => peer.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        serviceSharedHost.Visibility = peers.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var wanted = peers.ToHashSet();
        for (int i = serviceHostPeers.Count - 1; i >= 0; --i) if (!wanted.Contains(serviceHostPeers[i])) serviceHostPeers.RemoveAt(i);
        for (int i = 0; i < peers.Length; ++i) {
            if (i < serviceHostPeers.Count && ReferenceEquals(serviceHostPeers[i], peers[i])) continue;
            int found = serviceHostPeers.IndexOf(peers[i]);
            if (found >= 0) serviceHostPeers.Move(found, i); else serviceHostPeers.Insert(i, peers[i]);
        }
    }
    private void SelectRelatedService(string name)
    {
        if (servicesModel?.Find(name) is not { } target) return;
        UpdateServices(() => {
            if (!servicesModel.Visible.Contains(target)) {
                servicesModel.SetFilter(ServiceFilter.All); servicesModel.SetSharedHostsOnly(false); servicesModel.SetQuery("");
            }
            servicesModel.Select(name);
        });
        if (search.Text != servicesModel.Query) search.Text = servicesModel.Query;
        serviceList.ScrollIntoView(servicesModel.Selected, ScrollIntoViewAlignment.Leading);
    }
}
