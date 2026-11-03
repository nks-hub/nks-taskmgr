using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;
using WinRT;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private const double ServiceRowHeight = 44;
    private ServiceModel? servicesModel;
    private readonly Grid servicesPage = new(), serviceWorkspace = new();
    private readonly ListView serviceList = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly TextBlock serviceCount = Label("", 10, 0x95a4ad), serviceEmpty = Label("Načítám služby…", 12, 0x95a4ad);
    private readonly List<Button> serviceFilters = [];
    private readonly List<TextBlock> serviceCounters = [];
    private readonly ToggleButton sharedServiceHosts = Choice("Společný PID");
    private readonly ServiceHostTemplates serviceHostTemplates = new();
    private readonly ScrollViewer serviceDetailScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private bool updatingServices;
    private readonly RowDefinition serviceToolbarHeight = new() { Height = new GridLength(34) };

    private void BuildServices()
    {
        ConfigureServiceActions();
        servicesPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); servicesPage.RowDefinitions.Add(new RowDefinition());
        var filters = new[] { ServiceFilter.All, ServiceFilter.Running, ServiceFilter.Stopped, ServiceFilter.Other };
        serviceWorkspace.ColumnDefinitions.Add(new ColumnDefinition()); serviceWorkspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        serviceWorkspace.RowDefinitions.Add(new RowDefinition()); serviceWorkspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        var inventory = new Grid(); inventory.RowDefinitions.Add(serviceToolbarHeight); inventory.RowDefinitions.Add(new RowDefinition());
        var toolbar = new Grid { Padding = new Thickness(16, 0, 16, 0), BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(0, 0, 0, 1) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        foreach (var filter in filters) {
            string caption = filter == ServiceFilter.All ? "Všechny" : filter == ServiceFilter.Running ? "Běžící" : filter == ServiceFilter.Stopped ? "Zastavené" : "Ostatní";
            var button = Button("");
            var label = Label(caption, 11, 0x95a4ad); var count = Label("—", 11, 0xe9eef0);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(label); content.Children.Add(count); button.Content = content; serviceCounters.Add(count);
            AutomationProperties.SetName(button, caption);
            button.Height = 27; button.Padding = new Thickness(8, 0, 8, 0); button.FontSize = 11;
            button.Click += (_, _) => UpdateServices(() => servicesModel!.SetFilter(filter)); serviceFilters.Add(button); controls.Children.Add(button);
        }
        sharedServiceHosts.Margin = new Thickness(8, 0, 0, 0); sharedServiceHosts.Padding = new Thickness(8, 0, 8, 0);
        sharedServiceHosts.Checked += (_, _) => { if (!updatingServices) UpdateServices(() => servicesModel!.SetSharedHostsOnly(true)); };
        sharedServiceHosts.Unchecked += (_, _) => { if (!updatingServices) UpdateServices(() => servicesModel!.SetSharedHostsOnly(false)); };
        AutomationProperties.SetName(sharedServiceHosts, "Pouze služby se společným hlášeným PID");
        AutomationProperties.SetAutomationId(sharedServiceHosts, "services-shared-hosts");
        ToolTipService.SetToolTip(sharedServiceHosts, "Služby se stejným nenulovým PID hlášeným správcem služeb. Kombinuje se s filtrem stavu a hledáním.");
        controls.Children.Add(sharedServiceHosts);
        toolbar.Children.Add(new ScrollViewer { Content = controls, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        serviceCount.HorizontalAlignment = HorizontalAlignment.Right; serviceCount.VerticalAlignment = VerticalAlignment.Center;
        var serviceCommands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
        serviceCommands.Children.Add(serviceCount); serviceCommands.Children.Add(serviceActionsButton);
        Grid.SetColumn(serviceCommands, 1); toolbar.Children.Add(serviceCommands); inventory.Children.Add(toolbar);
        var table = new Grid { MinWidth = 706 }; table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) }); table.RowDefinitions.Add(new RowDefinition());
        var columns = new Grid { Padding = new Thickness(18, 0, 18, 0), Background = Display.Brush(0x1d252a) };
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), new GridLength(105), new GridLength(140), new GridLength(65), new GridLength(150) }) columns.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        columns.ColumnDefinitions[0].MinWidth = 210;
        string[] headings = ["Služba", "Stav", "Spouštění", "PID", "Účet"];
        for (int i = 0; i < headings.Length; ++i) {
            int column = i; var button = Button(headings[i]); button.BorderThickness = new Thickness(0); button.Padding = new Thickness(0); button.FontSize = 11;
            button.HorizontalContentAlignment = i == 3 ? HorizontalAlignment.Right : HorizontalAlignment.Left; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (i == 3) button.Margin = new Thickness(0, 0, 15, 0);
            button.Click += (_, _) => UpdateServices(() => servicesModel!.Sort(column)); Grid.SetColumn(button, i); columns.Children.Add(button);
        }
        table.Children.Add(columns);
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(FrameworkElement.HeightProperty, ServiceRowHeight));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, ServiceRowHeight));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); serviceList.ItemContainerStyle = style;
        serviceList.ItemTemplate = serviceHostTemplates["ServiceHostTemplate"].As<DataTemplate>();
        serviceList.ContainerContentChanging += (_, args) => {
            if (!args.InRecycleQueue && args.Item is ServiceRow row) AutomationProperties.SetName(args.ItemContainer, row.Title + " (" + row.Name + ")");
        };
        serviceList.SelectionChanged += (_, _) => {
            if (updatingServices || servicesModel is null) return;
            if (serviceList.SelectedItem is ServiceRow row) servicesModel.Select(row.Name);
            RefreshServiceDetail();
            RefreshServiceActions();
        };
        AutomationProperties.SetName(serviceList, "Systémové služby");
        AutomationProperties.SetAutomationId(serviceList, "services-list");
        Grid.SetRow(serviceList, 1); table.Children.Add(serviceList);
        serviceEmpty.HorizontalAlignment = HorizontalAlignment.Center; serviceEmpty.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(serviceEmpty, 1); table.Children.Add(serviceEmpty);
        var scroll = new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        scroll.SizeChanged += (_, args) => table.Width = Math.Max(706, args.NewSize.Width);
        Grid.SetRow(scroll, 1); inventory.Children.Add(scroll); serviceWorkspace.Children.Add(inventory);
        BuildServiceDetail(); Grid.SetColumn(serviceDetailScroll, 1); serviceWorkspace.Children.Add(serviceDetailScroll);
        var body = Card(serviceWorkspace); Grid.SetRow(body, 1); servicesPage.Children.Add(body);
        servicesPage.Visibility = Visibility.Collapsed; page.Children.Add(servicesPage);
    }

    private void UpdateServices(Action update)
    {
        updatingServices = true;
        try { update(); } finally { updatingServices = false; }
        RefreshServices();
    }
    private void TickServices(bool active)
    {
        if (servicesModel is null) return;
        servicesModel.SetActivity(active, model.Paused);
        if (!active) return;
        var restoreScroll = PreserveServiceScroll();
        updatingServices = true; bool changed;
        try { changed = servicesModel.Tick(); } finally { updatingServices = false; }
        if (changed || ticks % 10 == 0) { RefreshServices(); restoreScroll(); }
    }
    private void RefreshServices()
    {
        if (servicesModel is null) return;
        updatingServices = true;
        try { serviceList.SelectedItem = servicesModel.Selected; sharedServiceHosts.IsChecked = servicesModel.SharedHostsOnly; } finally { updatingServices = false; }
        serviceCount.Text = ServiceText.Count(servicesModel.Visible.Count);
        sharedServiceHosts.Content = "Společný PID (" + servicesModel.SharedHostCount + ")";
        serviceEmpty.Visibility = servicesModel.Visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        serviceEmpty.Text = servicesModel.Error.Length > 0 ? servicesModel.Error : servicesModel.Time == 0 ? "Načítám služby…" : "Žádná služba neodpovídá filtru.";
        var counts = new[] { servicesModel.All.Count, servicesModel.All.Count(row => row.State == 4), servicesModel.All.Count(row => row.State == 1), servicesModel.All.Count(row => row.State != 1 && row.State != 4) };
        for (int i = 0; i < serviceCounters.Count; ++i) {
            serviceCounters[i].Text = servicesModel.Time == 0 ? "—" : counts[i].ToString();
            serviceCounters[i].Foreground = Display.Brush(i == (int)servicesModel.Filter ? 0xedc08bu : 0xe9eef0u);
            serviceFilters[i].Background = Display.Brush(i == (int)servicesModel.Filter ? 0x28343bu : 0x1d252au);
        }
        RefreshServiceDetail();
        RefreshServiceActions();
    }
    private Action PreserveServiceScroll()
    {
        var source = servicesModel!;
        var scroll = Descendant<ScrollViewer>(serviceList);
        double offset = scroll?.VerticalOffset ?? 0;
        int index = (int)(offset / ServiceRowHeight);
        var anchor = index < source.Visible.Count ? source.Visible[index] : null;
        double remainder = offset % ServiceRowHeight;
        return () => {
            if (scroll is null || anchor is null) return;
            int current = source.Visible.IndexOf(anchor);
            double target = current >= 0 ? current * ServiceRowHeight + remainder : offset;
            if (Math.Abs(target - scroll.VerticalOffset) < 0.5) return;
            serviceList.UpdateLayout(); scroll.ChangeView(null, target, null, true);
        };
    }
    private void ResizeServices()
    {
        bool stacked = root.ActualWidth <= 1100;
        serviceWorkspace.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 330);
        serviceWorkspace.RowDefinitions[1].Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(serviceDetailScroll, stacked ? 0 : 1); Grid.SetRow(serviceDetailScroll, stacked ? 1 : 0);
        serviceDetailScroll.BorderThickness = stacked ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
    }
    private string ServicesFooter()
    {
        if (servicesModel is null) return "Načítám služby…";
        return (model.Paused ? "Pozastaveno" : servicesModel.Stale ? "Zastaralá data" : "Živá data") + " · " + servicesModel.All.Count + " služeb · stáří "
            + (servicesModel.Time == 0 ? "—" : Display.Number((NativeMonitor.MonotonicNowNs - servicesModel.Time) / 1_000_000_000d, " s"))
            + (servicesModel.Error.Length == 0 ? "" : " · " + servicesModel.Error);
    }
}
