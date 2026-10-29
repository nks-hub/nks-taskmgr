using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Windowing;
using NksTaskMgr.Native;
using Windows.Graphics;
using Windows.System;
using WinRT;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly MonitorModel model = new();
    private readonly Grid root = new() { Background = Display.Brush(0x171d21), RequestedTheme = ElementTheme.Dark };
    private readonly Grid page = new();
    private readonly Grid sidebar = new() { Background = Display.Brush(0x12181c), Width = 88 };
    private readonly TextBlock heading = Label("Výkon", 28, 0xe9eef0);
    private readonly TextBlock subtitle = Label("Živý přehled všech prostředků", 11, 0x95a4ad);
    private readonly TextBlock footer = Label("Načítám živá data…", 9, 0x95a4ad);
    private readonly TextBox search = new() { Width = 235, Height = 35, MinHeight = 0, FontSize = 11, PlaceholderText = "Hledat název nebo PID", Visibility = Visibility.Collapsed };
    private readonly Button pause = Button("Pozastavit"), interval = Button("1 s");
    private readonly Button performanceNav = Button("Výkon"), processesNav = Button("Procesy");
    private readonly ListView resourceList = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly ListView processList = new() { SelectionMode = ListViewSelectionMode.Extended, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly Grid performancePage = new(), processPage = new();
    private readonly Grid performanceBody = new(), chartGrid = new();
    private readonly TextBlock resourceEyebrow = Label("PROCESOR", 9, 0x95a4ad);
    private readonly TextBlock resourceTitle = Label("Procesor", 20, 0xe9eef0), resourceDescription = Label("", 10, 0x95a4ad);
    private readonly TextBlock resourceValue = Label("Načítám…", 42, 0x80c5c0);
    private readonly TextBlock retained = Label("", 9, 0x95a4ad);
    private readonly StackPanel cpuModes = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
    private readonly Grid processors = new();
    private readonly ChartView chart = new() { Height = 236 }, pageFile = new() { Height = 62 };
    private readonly TextBlock pageFileTitle = Label("", 10, 0x95a4ad);
    private readonly StackPanel facts = new() { Spacing = 16, Padding = new Thickness(22) };
    private readonly StackPanel consumers = new() { Spacing = 9 };
    private readonly TextBlock processCount = Label("", 10, 0x95a4ad), selectionTitle = Label("Vyberte proces", 14, 0xe9eef0), selectionFacts = Label("", 10, 0x95a4ad);
    private readonly StackPanel overview = new() { Orientation = Orientation.Horizontal, Spacing = 24 };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    private ResourceRow? selectedResource;
    private bool performance = true;
    private int cpuMode, rangeSeconds = 60, ticks;
    private string? smokePath;
    private bool smokeModelsChecked;

    public MainWindow()
    {
        App.Trace("Window objects created");
        Title = "NKS Task Manager · WinUI";
        AppWindow.Resize(new SizeInt32(1440, 920));
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 18, 24, 28);
        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 149, 164, 173);
        ConfigureResources(); BuildShell(); BuildPerformance(); BuildProcesses();
        App.Trace("Window layout created");
        Content = root;
        App.Trace("Content assigned");
        resourceList.ItemsSource = model.Resources;
        App.Trace("Resource source assigned");
        processList.ItemsSource = model.VisibleProcesses;
        App.Trace("Process source assigned");
        var resourceTemplate = Application.Current.Resources["ResourceTemplate"];
        App.Trace("Resource template: " + resourceTemplate.GetType().FullName);
        resourceList.ItemTemplate = resourceTemplate.As<DataTemplate>();
        processList.ItemTemplate = Application.Current.Resources["ProcessTemplate"].As<DataTemplate>();
        App.Trace("Templates assigned");
        resourceList.SelectionChanged += (_, _) => { selectedResource = resourceList.SelectedItem as ResourceRow; RefreshPerformance(); };
        processList.SelectionChanged += (_, _) => RefreshSelection();
        processList.PointerMoved += HoldRows;
        processList.PointerExited += (_, _) => model.SetHeld(false);
        processList.DoubleTapped += (_, _) => { if (model.Mode == ProcessMode.Tree && processList.SelectedItem is ProcessRow row) model.Toggle(row); };
        processList.KeyDown += (_, args) => {
            if ((args.Key == VirtualKey.Left || args.Key == VirtualKey.Right) && model.Mode == ProcessMode.Tree && processList.SelectedItem is ProcessRow row && row.HasChildren) {
                if ((args.Key == VirtualKey.Left && !row.Collapsed) || (args.Key == VirtualKey.Right && row.Collapsed)) model.Toggle(row);
                args.Handled = true;
            }
        };
        pause.Click += (_, _) => { model.SetPaused(!model.Paused); pause.Content = model.Paused ? "Pokračovat" : "Pozastavit"; };
        interval.Click += (_, _) => { model.SetInterval(model.Interval == 1000 ? 250u : 1000u); interval.Content = model.Interval == 1000 ? "1 s" : "250 ms"; };
        search.TextChanged += (_, _) => model.SetQuery(search.Text);
        processesNav.Click += (_, _) => ShowView(false);
        performanceNav.Click += (_, _) => ShowView(true);
        var find = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        find.Invoked += (_, args) => { ShowView(false); search.Focus(FocusState.Keyboard); args.Handled = true; };
        root.KeyboardAccelerators.Add(find);
        root.SizeChanged += (_, _) => Resize();
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += (_, _) => Tick(); timer.Start();
        Closed += (_, _) => { timer.Stop(); model.Dispose(); };
        AppWindow.Changed += (_, _) => Resize();
        smokePath = App.SmokePath;
        ShowView(true);
        App.Trace("Window initialized");
    }

    private static TextBlock Label(string text, double size, uint color) => new() {
        Text = text, FontSize = size, Foreground = Display.Brush(color), FontFamily = new FontFamily(size == 42 ? "Bahnschrift" : "Segoe UI"),
        FontWeight = size >= 14 && size != 42 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private static Button Button(string text) => new() {
        Content = text, MinHeight = 0, Height = 35, FontSize = 11, Padding = new Thickness(12, 0, 12, 0),
        Background = Display.Brush(0x1d252a), BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6)
    };
    private static Border Card(UIElement child, Thickness padding = default) => new() {
        Child = child, Padding = padding, Background = Display.Brush(0x1d252a), BorderBrush = Display.Brush(0x2c363d),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10)
    };

    private void ConfigureResources()
    {
        foreach (var key in new[] { "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed" })
            root.Resources[key] = Display.Brush(0x28343b);
        root.Resources["ListViewItemBackgroundPointerOver"] = Display.Brush(0x232d34);
        var itemStyle = new Style { TargetType = typeof(ListViewItem) };
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 52d));
        processList.ItemContainerStyle = itemStyle;
        var resourceStyle = new Style { TargetType = typeof(ListViewItem), BasedOn = itemStyle };
        resourceStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 59d));
        resourceList.ItemContainerStyle = resourceStyle;
        AutomationProperties.SetName(resourceList, "Jednotlivé prostředky systému");
        AutomationProperties.SetName(processList, "Živé procesy");
        AutomationProperties.SetName(search, "Hledat název procesu nebo PID");
    }

    private void BuildShell()
    {
        foreach (var height in new[] { new GridLength(49), new GridLength(101), new GridLength(1, GridUnitType.Star), new GridLength(30) }) root.RowDefinitions.Add(new RowDefinition { Height = height });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) }); root.ColumnDefinitions.Add(new ColumnDefinition());
        var title = new Grid { Background = Display.Brush(0x12181c), BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(0, 0, 0, 1) };
        var titleIcon = UiIcon.Create(true, 0xedc08b, 18); titleIcon.HorizontalAlignment = HorizontalAlignment.Left; titleIcon.Margin = new Thickness(23, 0, 0, 0); title.Children.Add(titleIcon);
        void TitleText(string value, double x, double size, uint color, bool bold = false) {
            var label = Label(value, size, color); label.HorizontalAlignment = HorizontalAlignment.Left; label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(x, 0, 0, 0); label.FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            if (size == 12) label.CharacterSpacing = 16; title.Children.Add(label);
        }
        TitleText("NKS", 51, 12, 0xe9eef0, true); TitleText("Task Manager", 83.109375, 12, 0x95a4ad); TitleText("Lokální počítač", 218, 10, 0x95a4ad);
        Grid.SetColumnSpan(title, 2); root.Children.Add(title); SetTitleBar(title);
        var nav = new StackPanel { Spacing = 4, Margin = new Thickness(8, 14, 8, 0) };
        var mark = Label("n.", 32, 0xedc08b); mark.Margin = new Thickness(18, 0, 0, 24); nav.Children.Add(mark);
        foreach (var button in new[] { processesNav, performanceNav }) {
            button.Height = 66; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.FontSize = 10; button.BorderThickness = new Thickness(0);
            var content = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(UiIcon.Create(button == performanceNav, 0x95a4ad)); content.Children.Add(Label(button == performanceNav ? "Výkon" : "Procesy", 9, 0x95a4ad));
            button.Content = content;
            nav.Children.Add(button);
        }
        sidebar.Children.Add(nav); Grid.SetRow(sidebar, 1); Grid.SetRowSpan(sidebar, 3); root.Children.Add(sidebar);
        var header = new Grid { Padding = new Thickness(30, 24, 30, 0) };
        var labels = new StackPanel { Spacing = 6 }; labels.Children.Add(heading); labels.Children.Add(subtitle); header.Children.Add(labels);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 11, 0, 0) };
        tools.Children.Add(search); tools.Children.Add(interval); tools.Children.Add(pause); header.Children.Add(tools);
        Grid.SetRow(header, 1); Grid.SetColumn(header, 1); root.Children.Add(header);
        page.Margin = new Thickness(30, 0, 30, 22); Grid.SetRow(page, 2); Grid.SetColumn(page, 1); root.Children.Add(page);
        var status = new Border { BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(25, 7, 10, 0), Child = footer };
        Grid.SetRow(status, 3); Grid.SetColumn(status, 1); root.Children.Add(status);
    }

    private void BuildPerformance()
    {
        performancePage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(278) });
        performancePage.ColumnDefinitions.Add(new ColumnDefinition()); performancePage.ColumnSpacing = 20;
        var inventory = new Grid { Padding = new Thickness(10, 18, 10, 10) };
        inventory.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) }); inventory.RowDefinitions.Add(new RowDefinition());
        var inventoryLabel = Label("Prostředky", 13, 0xe9eef0); inventoryLabel.Margin = new Thickness(9, 0, 0, 0); inventory.Children.Add(inventoryLabel);
        Grid.SetRow(resourceList, 1); inventory.Children.Add(resourceList); performancePage.Children.Add(Card(inventory));
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = performanceBody };
        performanceBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        performanceBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        performanceBody.RowSpacing = 24;
        Grid.SetColumn(scroll, 1); performancePage.Children.Add(scroll);
        chartGrid.ColumnDefinitions.Add(new ColumnDefinition()); chartGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(242) }); chartGrid.ColumnSpacing = 14;
        chartGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); chartGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel { Spacing = 12, Padding = new Thickness(22, 22, 22, 18) };
        var resourceHeading = new Grid(); resourceHeading.Children.Add(resourceEyebrow);
        var range = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (int seconds in new[] { 60, 600 }) {
            var button = Button(seconds == 60 ? "60 s" : "10 min"); button.Height = 27;
            button.Click += (_, _) => { rangeSeconds = seconds; RefreshPerformance(); }; range.Children.Add(button);
        }
        resourceHeading.Children.Add(range); content.Children.Add(resourceHeading);
        content.Children.Add(resourceTitle); content.Children.Add(resourceDescription); content.Children.Add(resourceValue);
        foreach (var mode in new[] { "Celkem", "Logické", "Překrytí" }) {
            int index = cpuModes.Children.Count; var button = Button(mode); button.Height = 25;
            button.Click += (_, _) => { cpuMode = index; RefreshPerformance(); }; cpuModes.Children.Add(button);
        }
        content.Children.Add(cpuModes); content.Children.Add(chart); content.Children.Add(processors); content.Children.Add(retained);
        content.Children.Add(pageFileTitle); content.Children.Add(pageFile);
        chartGrid.Children.Add(Card(content));
        var factCard = Card(facts); Grid.SetColumn(factCard, 1); chartGrid.Children.Add(factCard);
        performanceBody.Children.Add(chartGrid);
        Grid.SetRow(consumers, 1); performanceBody.Children.Add(consumers);
        page.Children.Add(performancePage);
    }

    private void BuildProcesses()
    {
        foreach (var height in new[] { new GridLength(108), new GridLength(51), new GridLength(39), new GridLength(1, GridUnitType.Star), new GridLength(92) })
            processPage.RowDefinitions.Add(new RowDefinition { Height = height });
        processPage.RowSpacing = 0;
        var system = Card(overview, new Thickness(23, 20, 23, 20)); processPage.Children.Add(system);
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        foreach (var mode in new[] { ProcessMode.All, ProcessMode.Active, ProcessMode.Tree }) {
            var button = Button(mode == ProcessMode.All ? "Všechny procesy" : mode == ProcessMode.Active ? "Aktivní" : "Strom"); button.Height = 29;
            button.Click += (_, _) => { model.SetMode(mode); RefreshProcesses(); }; filters.Children.Add(button);
        }
        filters.Children.Add(processCount); processCount.Margin = new Thickness(20, 8, 0, 0); Grid.SetRow(filters, 1); processPage.Children.Add(filters);
        var columns = new Grid { Background = Display.Brush(0x1d252a), Padding = new Thickness(16, 0, 16, 0) };
        foreach (var width in new[] { new GridLength(3, GridUnitType.Star), new GridLength(70), new GridLength(86), new GridLength(120), new GridLength(126), new GridLength(108), new GridLength(108) })
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        columns.ColumnDefinitions[0].MinWidth = 210;
        var names = new[] { "Proces", "PID", "CPU", "Pracovní sada", "Soukromá paměť", "Čtení I/O", "Zápis I/O" };
        for (int i = 0; i < names.Length; ++i) {
            int index = i; var button = Button(names[i]); button.FontSize = 9; button.BorderThickness = new Thickness(0); button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Click += (_, _) => { model.Sort(index); RefreshProcesses(); }; Grid.SetColumn(button, i); columns.Children.Add(button);
        }
        Grid.SetRow(columns, 2); processPage.Children.Add(columns);
        Grid.SetRow(processList, 3); processPage.Children.Add(processList);
        var dock = new StackPanel { Padding = new Thickness(21, 18, 21, 18), Spacing = 12 };
        dock.Children.Add(selectionTitle); dock.Children.Add(selectionFacts);
        var selection = Card(dock); Grid.SetRow(selection, 4); processPage.Children.Add(selection);
        processPage.Visibility = Visibility.Collapsed; page.Children.Add(processPage);
    }

    private void ShowView(bool value)
    {
        model.SetHeld(false); performance = value;
        heading.Text = value ? "Výkon" : "Procesy"; subtitle.Text = value ? "Živý přehled všech prostředků" : "Aplikace, prostředky a jejich souvislosti.";
        performancePage.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        processPage.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        search.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        performanceNav.Background = Display.Brush(value ? 0x3b3024u : 0x12181cu);
        processesNav.Background = Display.Brush(value ? 0x12181cu : 0x3b3024u);
        if (value) RefreshPerformance(); else RefreshProcesses();
    }

    private void Resize()
    {
        if (root.ActualWidth <= 0) return;
        double width = root.ActualWidth;
        root.ColumnDefinitions[0].Width = new GridLength(width <= 1000 ? 68 : 88); sidebar.Width = width <= 1000 ? 68 : 88;
        page.Margin = new Thickness(width <= 1000 ? 20 : 30, 0, width <= 1000 ? 20 : 30, 22);
        performancePage.ColumnDefinitions[0].Width = new GridLength(width <= 780 ? 180 : width <= 1000 ? 225 : 278);
        var factCard = (FrameworkElement)chartGrid.Children[1];
        Grid.SetColumn(factCard, width <= 1250 ? 0 : 1); Grid.SetRow(factCard, width <= 1250 ? 1 : 0);
        chartGrid.ColumnDefinitions[1].Width = width <= 1250 ? new GridLength(0) : new GridLength(242);
        processPage.RowDefinitions[0].Height = root.ActualHeight < 650 ? new GridLength(0) : new GridLength(108);
        RefreshPerformance();
    }

    private void Tick()
    {
        try {
            bool changed = model.Tick();
            bool minimized = AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized;
            if (changed && !minimized) {
                if (selectedResource is null || !model.Resources.Contains(selectedResource)) resourceList.SelectedItem = model.Resources.FirstOrDefault();
                if (performance) RefreshPerformance(); else RefreshProcesses();
            }
            if (++ticks % 10 == 0) footer.Text = (model.Paused ? "Pozastaveno" : "Živá data") + $" · {model.Processes.Count} procesů · stáří "
                + Display.Number((NativeMonitor.MonotonicNowNs - model.ProcessTime) / 1_000_000_000d, " s")
                + (model.ProcessError.Length == 0 ? "" : " · " + model.ProcessError) + (model.ResourceError.Length == 0 ? "" : " · " + model.ResourceError);
            RunSmoke();
        } catch (NativeMonitorException error) { footer.Text = error.Message; }
    }

    private void HoldRows(object sender, PointerRoutedEventArgs args)
    {
        var element = args.OriginalSource as DependencyObject;
        bool row = false;
        while (element is not null && element != processList) {
            if (element is ListViewItem item && item.Content is ProcessRow) { row = true; break; }
            element = VisualTreeHelper.GetParent(element);
        }
        model.SetHeld(row);
    }

    private void RefreshProcesses()
    {
        processCount.Text = model.VisibleProcesses.Count + (model.Held ? " položek · pořadí přidrženo" : " položek");
        overview.Children.Clear();
        foreach (var resource in model.Resources.Where(row => row.Kind is ResourceKind.Cpu or ResourceKind.Memory)) {
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(Label(resource.Title, 10, 0x95a4ad)); stack.Children.Add(Label(resource.Value, 20, Display.ResourceColor(resource.Kind)));
            var button = Button(""); button.Content = stack; button.Height = 68; button.MinWidth = 180;
            button.Click += (_, _) => { resourceList.SelectedItem = resource; ShowView(true); }; overview.Children.Add(button);
        }
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (processList.SelectedItem is not ProcessRow row) { selectionTitle.Text = "Vyberte proces"; selectionFacts.Text = "Ctrl a Shift vyberou více řádků."; return; }
        selectionTitle.Text = row.Ended ? row.Name + " · ukončeno" : row.Name;
        selectionFacts.Text = $"PID {row.PidText} · {row.Threads} vláken · {row.Handles} popisovačů · CPU {row.CpuText} · {row.WorkingText}";
    }

    private void RunSmoke()
    {
        if (smokePath is null || ticks < 50) return;
        bool ready = model.Processes.Any(row => row.Key.Pid == Environment.ProcessId && row.Key.Generation != 0)
            && model.Resources.Count > 2 && resourceList.ActualHeight > 0 && selectedResource is not null;
        if (ready && !smokeModelsChecked) {
            WinUiChecks.ModelInvariants(model); smokeModelsChecked = true;
            ShowView(false); processList.SelectedItem = model.Processes.First(row => row.Key.Pid == Environment.ProcessId);
        }
        if (smokeModelsChecked && ticks == 60) { resourceList.SelectedItem = model.Resources.First(row => row.Kind == ResourceKind.Memory); ShowView(true); }
        if (smokeModelsChecked && ticks == 70) { resourceList.SelectedItem = model.Resources.First(row => row.Kind == ResourceKind.Cpu); cpuMode = 1; RefreshPerformance(); }
        if (smokeModelsChecked && ticks == 80) {
            if (coreCharts.Count != model.Processors.Count || coreCharts.Values.Any(core => core.ActualWidth <= 0)) throw new InvalidOperationException("Logical CPU charts did not render.");
            cpuMode = 2; RefreshPerformance();
        }
        if ((smokeModelsChecked && ticks >= 90) || ticks > 150) {
            App.Trace($"aot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported};ready={ready};models={smokeModelsChecked};processes={model.Processes.Count};resources={model.Resources.Count};charts={coreCharts.Count}");
            Close();
        }
    }
}
