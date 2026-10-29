using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Windows.System;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly TextBlock serviceTitle = DetailLabel("Vyberte službu", 22), serviceName = DetailLabel("", 10), serviceDescription = DetailLabel("", 11);
    private readonly TextBlock serviceState = DetailLabel("", 11), serviceStartup = DetailLabel("", 11), serviceAccount = DetailLabel("", 11);
    private readonly TextBlock serviceGroup = DetailLabel("", 11), serviceCommand = DetailLabel("", 11), serviceDetailNote = DetailLabel("", 10);
    private readonly Button serviceHost = Button("Hostitelský proces →"), serviceOverviewTab = Button("Přehled"), serviceDependenciesTab = Button("Závislosti");
    private readonly StackPanel serviceOverview = new() { Spacing = 16 }, serviceDependencies = new() { Spacing = 8 };
    private readonly StackPanel serviceTabs = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
    private readonly List<(Button Button, string Name)> dependencyButtons = [];
    private string dependencySelection = "";
    private string[] shownDependencies = [], shownDependents = [];
    private uint shownDependencyError = uint.MaxValue, shownDependentError = uint.MaxValue;
    private bool dependenciesSelected;

    private static TextBlock DetailLabel(string value, double size) => new() {
        Text = value, FontSize = size, FontFamily = new FontFamily("Segoe UI"), Foreground = Display.Brush(size < 12 ? 0x95a4adu : 0xe9eef0u),
        FontWeight = size >= 14 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
    };
    private void BuildServiceDetail()
    {
        serviceDetailScroll.Background = Display.Brush(0x171d21); serviceDetailScroll.BorderBrush = Display.Brush(0x2c363d);
        var content = new StackPanel { Spacing = 16, Padding = new Thickness(23, 21, 23, 21) };
        var emblem = new Border { Width = 43, Height = 43, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), BorderBrush = Display.Brush(0x2c363d), Background = Display.Brush(0x1d252a), HorizontalAlignment = HorizontalAlignment.Left, Child = UiIcon.Create("services", 0xedc08b) };
        content.Children.Add(emblem); content.Children.Add(serviceTitle); content.Children.Add(serviceName); content.Children.Add(serviceDescription);
        serviceName.FontFamily = new FontFamily("Consolas");
        serviceHost.HorizontalAlignment = HorizontalAlignment.Left;
        serviceHost.Click += (_, _) => OpenServiceHost();
        content.Children.Add(serviceHost);
        foreach (var button in new[] { serviceOverviewTab, serviceDependenciesTab }) { button.Height = 27; serviceTabs.Children.Add(button); }
        serviceOverviewTab.Click += (_, _) => { dependenciesSelected = false; RefreshServiceDetail(); };
        serviceDependenciesTab.Click += (_, _) => { dependenciesSelected = true; RefreshServiceDetail(); }; content.Children.Add(serviceTabs);
        void Fact(string label, TextBlock value) { var group = new StackPanel { Spacing = 6 }; group.Children.Add(DetailLabel(label, 9)); group.Children.Add(value); serviceOverview.Children.Add(group); }
        Fact("Stav služby", serviceState); Fact("Spouštění", serviceStartup); Fact("Účet", serviceAccount); Fact("Skupina", serviceGroup); Fact("Příkaz", serviceCommand);
        content.Children.Add(serviceOverview); content.Children.Add(serviceDependencies); content.Children.Add(serviceDetailNote); serviceDetailScroll.Content = content;
    }
    private void OpenServiceHost()
    {
        if (servicesModel?.Host(model) is not { } host) return;
        processQuery = ""; model.SetQuery(""); model.SetMode(ProcessMode.All); ShowView(false);
        processList.SelectedItems.Clear(); processList.SelectedItem = host; processList.ScrollIntoView(host, ScrollIntoViewAlignment.Leading);
    }
    private void RefreshServiceDetail()
    {
        var row = servicesModel?.Selected; var detail = servicesModel?.Detail;
        if (row is null) detail = null;
        serviceTitle.Text = row?.Title ?? "Vyberte službu"; serviceName.Text = row?.Name ?? "";
        serviceDescription.Text = row is null ? "Výběr v seznamu otevře konfiguraci a závislosti služby." : detail is null
            ? servicesModel!.DetailError.Length > 0 ? servicesModel.DetailError : model.Paused ? "Obnovte sběr pro načtení detailu." : "Načítám detail…"
            : detail.DescriptionError != 0 ? ServiceText.Error(detail.DescriptionError) : detail.Description;
        var config = detail?.Config ?? row?.Config;
        serviceState.Text = row is null ? "—" : detail is not null && detail.StatusError != 0 ? ServiceText.Error(detail.StatusError) : ServiceText.State(detail?.State ?? row.State);
        serviceStartup.Text = config?.Startup ?? "—";
        serviceAccount.Text = config is null ? "—" : config.Error != 0 ? ServiceText.Error(config.Error) : config.Account;
        serviceGroup.Text = config is null ? "—" : config.Error != 0 ? ServiceText.Error(config.Error) : config.Group.Length == 0 ? "Bez skupiny" : config.Group;
        serviceCommand.Text = config is null ? "—" : config.Error != 0 ? ServiceText.Error(config.Error) : config.Command;
        serviceHost.IsEnabled = servicesModel?.Host(model) is not null;
        serviceHost.Visibility = serviceTabs.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        string hostHint = serviceHost.IsEnabled ? "Otevřít hostitelský proces." : model.Paused ? "Obnovte sběr pro otevření hostitele."
            : detail?.HostError > 0 ? "Hostitele nelze ověřit: " + ServiceText.Error(detail.HostError)
            : row?.State != 4 ? "Služba není spuštěná." : "Hostitelský proces se změnil nebo data nejsou aktuální.";
        ToolTipService.SetToolTip(serviceHost, hostHint); AutomationProperties.SetHelpText(serviceHost, hostHint);
        serviceOverview.Visibility = row is not null && !dependenciesSelected ? Visibility.Visible : Visibility.Collapsed;
        serviceDependencies.Visibility = row is not null && dependenciesSelected ? Visibility.Visible : Visibility.Collapsed;
        serviceOverviewTab.Background = Display.Brush(dependenciesSelected ? 0x1d252au : 0x28343bu);
        serviceDependenciesTab.Background = Display.Brush(dependenciesSelected ? 0x28343bu : 0x1d252au);
        serviceDetailNote.Text = row is null ? "" : servicesModel!.DetailError.Length > 0 ? servicesModel.DetailError : detail is null ? ""
            : "Detail · stáří " + Display.Number((NksTaskMgr.Native.NativeMonitor.MonotonicNowNs - detail.Time) / 1_000_000_000d, " s")
                + (servicesModel.Stale ? " · zastaralý seznam" : "");
        var dependencies = config?.Dependencies ?? []; var dependents = detail?.Dependents ?? [];
        uint dependencyError = config?.Error ?? 232, dependentError = detail?.DependentsError ?? 232;
        if (dependencySelection != (row?.Name ?? "") || dependencyError != shownDependencyError || dependentError != shownDependentError
            || !shownDependencies.SequenceEqual(dependencies) || !shownDependents.SequenceEqual(dependents)) {
            dependencySelection = row?.Name ?? ""; shownDependencies = dependencies; shownDependents = dependents;
            shownDependencyError = dependencyError; shownDependentError = dependentError; serviceDependencies.Children.Clear(); dependencyButtons.Clear();
            AddDependencies("Potřebuje ke spuštění", dependencies, dependencyError); AddDependencies("Navazující služby", dependents, dependentError);
        }
        foreach (var (button, name) in dependencyButtons) button.IsEnabled = !name.StartsWith('+') && servicesModel?.Find(name) is not null;
    }
    private void AddDependencies(string heading, string[] names, uint error)
    {
        serviceDependencies.Children.Add(DetailLabel(heading, 11));
        if (error != 0) { serviceDependencies.Children.Add(DetailLabel(ServiceText.Error(error), 10)); return; }
        if (names.Length == 0) { serviceDependencies.Children.Add(DetailLabel("Žádné", 10)); return; }
        foreach (string name in names) {
            var button = Button(name.StartsWith('+') ? "Skupina: " + name[1..] : name + " →");
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetName(button, "Závislost " + name);
            button.Click += (_, _) => {
                if (servicesModel?.Find(name) is null || name.StartsWith('+')) return;
                UpdateServices(() => { servicesModel.SetFilter(ServiceFilter.All); servicesModel.SetQuery(""); servicesModel.Select(name); });
                search.Text = ""; serviceList.ScrollIntoView(servicesModel.Selected, ScrollIntoViewAlignment.Leading);
            };
            dependencyButtons.Add((button, name)); serviceDependencies.Children.Add(button);
        }
    }
}
