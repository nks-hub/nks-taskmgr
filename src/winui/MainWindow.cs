using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using NksTaskMgr.Native;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using WinRT;
using System.Runtime.InteropServices;

namespace NksTaskMgr.WinUI;
internal enum DesktopView { Processes, Performance, Services, Hardware }

public sealed partial class MainWindow : Window
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);
    private readonly nint windowHandle;
    private readonly MonitorModel model = new();
    private readonly Grid root = new() { Background = Display.Brush(0x171d21) };
    private readonly Grid page = new();
    private readonly Grid sidebar = new() { Background = Display.Brush(0x12181c), Width = 88 };
    private readonly TextBlock heading = Label("Výkon", 28, 0xe9eef0);
    private readonly TextBlock subtitle = Label("Jeden pohled. Společná časová osa.", 11, 0x95a4ad);
    private readonly TextBlock footer = Label("Načítám živá data…", 9, 0x95a4ad);
    private readonly TextBox search = new() { Width = 235, Height = 35, MinHeight = 0, FontSize = 11, PlaceholderText = "Hledat název nebo PID", Visibility = Visibility.Collapsed };
    private readonly Button pause = Button("Pozastavit"), interval = Button("1 s");
    private readonly Button performanceNav = Button("Výkon"), processesNav = Button("Procesy"), servicesNav = Button("Služby");
    private readonly Button hardwareNav = Button("Sestava");
    private readonly ListView resourceList = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly ListView processList = new() { SelectionMode = ListViewSelectionMode.Extended, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly Grid performancePage = new(), processPage = new();
    private ProcessTable processTable = null!;
    private readonly Grid performanceBody = new(), chartGrid = new();
    private readonly TextBlock resourceEyebrow = Label("PROCESOR", 9, 0x95a4ad);
    private readonly TextBlock resourceTitle = Label("Procesor", 20, 0xe9eef0), resourceDescription = Label("", 10, 0x95a4ad);
    private readonly TextBlock resourceValue = Label("Načítám…", 42, 0x80c5c0);
    private readonly TextBlock resourceUnit = Label("", 13, 0x95a4ad);
    private readonly List<ToggleButton> rangeButtons = [], cpuButtons = [];
    private readonly TextBlock retained = Label("", 9, 0x95a4ad);
    private readonly TextBlock resourceCount = Label("", 9, 0x95a4ad);
    private readonly StackPanel cpuModes = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
    private readonly ToggleButton scaleChoice = Choice("Auto měřítko");
    private readonly Grid plotToolbar = new() { Margin = new Thickness(0, 12, 0, 12), RowSpacing = 6 };
    private readonly StackPanel chartControls = new() { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Grid processors = new();
    private readonly ChartView chart = new() { Height = 260 }, pageFile = new() { Height = 94 };
    private readonly TextBlock pageFileTitle = Label("", 10, 0x95a4ad);
    private readonly StackPanel facts = new() { Spacing = 13, Padding = new Thickness(22) };
    private readonly StackPanel consumers = new() { Spacing = 9 };
    private readonly TextBlock processCount = Label("", 10, 0x95a4ad), selectionTitle = Label("Vyberte proces", 14, 0xe9eef0), selectionFacts = Label("", 10, 0x95a4ad);
    private readonly StackPanel overview = new() { Orientation = Orientation.Horizontal, Spacing = 24 };
    private readonly Dictionary<ResourceKind, (TextBlock Caption, TextBlock Value)> overviewValues = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    private ResourceRow? selectedResource;
    private DesktopView view = DesktopView.Performance;
    private bool performance => view == DesktopView.Performance;
    private string processQuery = "";
    private int cpuMode, rangeSeconds = 60, ticks;
    private string? smokePath;
    private bool smokeModelsChecked;
    private bool smokeMinimizedChecked;
    private Action? verifyMinimized;
    private Action? verifyProcessTable;
    private bool smokeColumnsChecked;
    private Task<byte[]?[]>? smokeIcons;
    private Task? smokeSharedIcon;
    private Task? smokeIconIsolation;
    private int smokeIconsAt;
    private int smokeMinimizedAt, smokeReadyAt;
    private int smokeServicesStage, smokeServicesAt;
    private long smokeServiceTime;
    private bool smokeServicesChecked;

    public MainWindow()
    {
        App.Trace("Window objects created");
        windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (App.SmokePath is not null) App.Trace("Presenter=" + AppWindow.Presenter.GetType().FullName + ";overlapped=" + (AppWindow.Presenter is OverlappedPresenter));
        Title = "NKS Task Manager · WinUI";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.Resize(new SizeInt32(1440, 920));
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        Application.Current.Resources["ContentControlThemeFontFamily"] = UiTypography.Font;
        Application.Current.Resources["TextControlThemeFontFamily"] = UiTypography.Font;
        ConfigureResources(); ConfigureSnapshotExport(); ConfigureSnapshotComparison(); ConfigureProcessDetails(); ConfigureProcessActions(); ConfigureProcessDump(); ConfigureProcessEvents(); ConfigureGlobalObjectSearch(); BuildShell(); ConfigureAppearance(); ConfigureUpdates(); BuildPerformance(); BuildProcesses(); BuildServices(); BuildHardware(); InitializeContextMenus();
        App.Trace("Window layout created");
        Content = root;
        App.Trace("Content assigned");
        resourceList.ItemsSource = model.Resources;
        App.Trace("Resource source assigned");
        processList.ItemsSource = processSlots;
        processList.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
        PreserveProcessView(model);
        App.Trace("Process source assigned");
        var resourceTemplate = Application.Current.Resources["ResourceTemplate"];
        App.Trace("Resource template: " + resourceTemplate.GetType().FullName);
        resourceList.ItemTemplate = resourceTemplate.As<DataTemplate>();
        processList.ItemTemplate = Application.Current.Resources["ProcessTemplate"].As<DataTemplate>();
        App.Trace("Templates assigned");
        resourceList.SelectionChanged += (_, _) => { selectedResource = resourceList.SelectedItem as ResourceRow; RefreshPerformance(); };
        processList.SelectionChanged += (_, _) => RefreshSelection();
        processList.PointerMoved += HoldRows;
        processList.PointerExited += (_, _) => { if (!IsIconic(windowHandle)) model.SetHeld(false); };
        processList.DoubleTapped += (_, _) => { if (model.Mode == ProcessMode.Tree && SelectedProcess is { } row) model.Toggle(row); };
        processList.KeyDown += (_, args) => {
            if ((args.Key == VirtualKey.Left || args.Key == VirtualKey.Right) && model.Mode == ProcessMode.Tree && SelectedProcess is { } row && row.HasChildren) {
                if ((args.Key == VirtualKey.Left && !row.Collapsed) || (args.Key == VirtualKey.Right && row.Collapsed)) model.Toggle(row);
                args.Handled = true;
            }
        };
        pause.Click += (_, _) => { model.SetPaused(!model.Paused); pause.Content = model.Paused ? "Pokračovat" : "Pozastavit"; };
        interval.Click += (_, _) => { model.SetInterval(model.Interval == 1000 ? 250u : 1000u); interval.Content = model.Interval == 1000 ? "1 s" : "250 ms"; };
        search.TextChanged += (_, _) => {
            if (view == DesktopView.Services) UpdateServices(() => servicesModel!.SetQuery(search.Text));
            else { processQuery = search.Text; model.SetQuery(search.Text); }
        };
        processesNav.Click += (_, _) => ShowView(false);
        performanceNav.Click += (_, _) => ShowView(true);
        servicesNav.Click += (_, _) => ShowView(DesktopView.Services);
        hardwareNav.Click += (_, _) => ShowView(DesktopView.Hardware);
        root.PreviewKeyDown += (_, args) => {
            if (args.Handled || args.Key != VirtualKey.F) return;
            static bool IsDown(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
            if (!IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift) || IsDown(VirtualKey.Menu)
                || IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) return;
            if (view is DesktopView.Performance or DesktopView.Hardware) ShowView(false);
            search.Focus(FocusState.Keyboard);
            args.Handled = true;
        };
        AutomationProperties.SetAcceleratorKey(search, "Ctrl+F");
        ToolTipService.SetToolTip(search, "Hledat (Ctrl+F)");
        root.SizeChanged += (_, _) => Resize();
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += (_, _) => Tick(); timer.Start();
        Closed += (_, _) => { timer.Stop(); IconWorkers.Stop(); servicesModel?.Dispose(); model.Dispose(); };
        AppWindow.Changed += (_, _) => Resize();
        smokePath = App.SmokePath;
        ShowView(Environment.GetCommandLineArgs().Contains("--view=Services", StringComparer.OrdinalIgnoreCase) ? DesktopView.Services
            : Environment.GetCommandLineArgs().Contains("--view=Processes", StringComparer.OrdinalIgnoreCase) ? DesktopView.Processes : DesktopView.Performance);
        App.Trace("Window initialized");
    }

    private static TextBlock Label(string text, double size, uint color) => new() {
        Text = text, FontSize = Math.Max(11, size), Foreground = Display.Brush(color), FontFamily = UiTypography.Font,
        FontWeight = size >= 14 && size != 42 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private static Button Button(string text) => new() {
        Content = text, MinHeight = 0, Height = 35, FontSize = 12, FontFamily = UiTypography.Font, Padding = new Thickness(12, 0, 12, 0),
        Background = Display.Brush(0x1d252a), BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6)
    };
    private static Border Card(UIElement child, Thickness padding = default) => new() {
        Child = child, Padding = padding, Background = Display.Brush(0x1d252a), BorderBrush = Display.Brush(0x2c363d),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10)
    };
    private static ToggleButton Choice(string text) => new() {
        Content = text, MinHeight = 0, Height = 27, FontSize = 11, FontFamily = UiTypography.Font, Padding = new Thickness(12, 0, 12, 0),
        Background = Display.Brush(0x1d252a), BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6)
    };

    private void ConfigureResources()
    {
        foreach (var state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed" }) {
            root.Resources["ToggleButtonBackground" + state] = Display.Brush(state == "CheckedPointerOver" ? 0x33414au : state == "CheckedPressed" ? 0x253039u : 0x28343bu);
            root.Resources["ToggleButtonForeground" + state] = performanceInk;
            root.Resources["ToggleButtonBorderBrush" + state] = Display.Brush(0x2c363d);
        }
        foreach (var key in new[] { "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed" })
            root.Resources[key] = Display.Brush(0x28343b);
        root.Resources["ListViewItemBackgroundPointerOver"] = Display.Brush(0x232d34);
        var itemStyle = new Style { TargetType = typeof(ListViewItem) };
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34d));
        processList.ItemContainerStyle = itemStyle;
        var resourceStyle = new Style { TargetType = typeof(ListViewItem), BasedOn = itemStyle };
        resourceStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 59d));
        resourceList.ItemContainerStyle = resourceStyle;
        AutomationProperties.SetName(resourceList, "Jednotlivé prostředky systému");
        AutomationProperties.SetName(processList, "Živé procesy");
        AutomationProperties.SetAutomationId(processList, "processes-list");
        AutomationProperties.SetName(search, "Hledat název procesu nebo PID");
        search.FontFamily = UiTypography.Font; search.FontSize = 12;
    }

    private void BuildShell()
    {
        foreach (var height in new[] { new GridLength(49), new GridLength(101), new GridLength(1, GridUnitType.Star), new GridLength(30) }) root.RowDefinitions.Add(new RowDefinition { Height = height });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) }); root.ColumnDefinitions.Add(new ColumnDefinition());
        var title = new Grid { Background = Display.Brush(0x12181c), BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(0, 0, 0, 1) };
        var titleIcon = new Image { Width = 22, Height = 22, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "app.png"))), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(22, 0, 0, 0) };
        title.Children.Add(titleIcon);
        void TitleText(string value, double x, double size, uint color, bool bold = false) {
            var label = Label(value, size, color); label.HorizontalAlignment = HorizontalAlignment.Left; label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(x, 0, 0, 0); label.FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            if (size == 12) label.CharacterSpacing = 16; title.Children.Add(label);
        }
        TitleText("NKS", 51, 12, 0xe9eef0, true); TitleText("Task Manager", 83.109375, 12, 0x95a4ad); TitleText("Lokální počítač", 218, 10, 0x95a4ad);
        Grid.SetColumnSpan(title, 2); root.Children.Add(title); SetTitleBar(title);
        var nav = new StackPanel { Spacing = 4, Margin = new Thickness(8, 14, 8, 0) };
        var mark = Label("n", 32, 0xe9eef0); mark.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        mark.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = ".", Foreground = Display.Brush(0xedc08b) });
        mark.CharacterSpacing = -125; mark.Margin = new Thickness(23, -1, 0, 18); nav.Children.Add(mark);
        foreach (var button in new[] { processesNav, performanceNav, servicesNav, hardwareNav }) {
            button.Height = 66; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.FontSize = 10; button.BorderThickness = new Thickness(0);
            button.Padding = new Thickness(4, 0, 4, 0);
            var content = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            string name = button == performanceNav ? "Výkon" : button == servicesNav ? "Služby" : button == hardwareNav ? "Sestava" : "Procesy";
            AutomationProperties.SetName(button, name);
            content.Children.Add(UiIcon.Create(button == performanceNav ? "performance" : button == servicesNav ? "services" : button == hardwareNav ? "hardware" : "processes", 0x95a4ad)); content.Children.Add(Label(name, 9, 0x95a4ad));
            button.Content = content;
            nav.Children.Add(button);
        }
        sidebar.Children.Add(nav); Grid.SetRow(sidebar, 1); Grid.SetRowSpan(sidebar, 3); root.Children.Add(sidebar);
        var header = new Grid { Padding = new Thickness(30, 24, 30, 0) };
        heading.CharacterSpacing = -25;
        var labels = new StackPanel { Spacing = 6, Margin = new Thickness(1, -2, 0, 0) }; labels.Children.Add(heading); labels.Children.Add(subtitle); header.Children.Add(labels);
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
        inventory.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        inventory.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); inventory.RowDefinitions.Add(new RowDefinition());
        var inventoryHeader = new Grid { Margin = new Thickness(8, 1, 9, 0) };
        var inventoryLabel = Label("Prostředky", 13, 0xe9eef0); inventoryLabel.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        resourceCount.HorizontalAlignment = HorizontalAlignment.Right; resourceCount.VerticalAlignment = VerticalAlignment.Center;
        inventoryHeader.Children.Add(inventoryLabel); inventoryHeader.Children.Add(resourceCount); inventory.Children.Add(inventoryHeader);
        var networkFilter = BuildNetworkFilter(); Grid.SetRow(networkFilter, 1); inventory.Children.Add(networkFilter);
        Grid.SetRow(resourceList, 2); inventory.Children.Add(resourceList); performancePage.Children.Add(Card(inventory));
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = performanceBody };
        performanceBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        performanceBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        performanceBody.RowSpacing = 24;
        Grid.SetColumn(scroll, 1); performancePage.Children.Add(scroll);
        chartGrid.ColumnDefinitions.Add(new ColumnDefinition()); chartGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(242) }); chartGrid.ColumnSpacing = 14;
        chartGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); chartGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel { Padding = new Thickness(22, 20, 22, 16) };
        var resourceHeading = new Grid { ColumnSpacing = 15 };
        resourceHeading.ColumnDefinitions.Add(new ColumnDefinition()); resourceHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headingText = new StackPanel { Spacing = 4 };
        resourceTitle.CharacterSpacing = -15; headingText.Children.Add(resourceTitle);
        headingText.Children.Add(resourceDescription); resourceDescription.TextTrimming = TextTrimming.CharacterEllipsis;
        resourceHeading.Children.Add(headingText);
        scaleChoice.IsChecked = true;
        scaleChoice.Click += (_, _) => RefreshPerformance();
        ToolTipService.SetToolTip(scaleChoice, "Zapnuto: měřítko podle nejvyššího vzorku. Vypnuto: 100 % nebo kapacita zařízení.");
        chartControls.Children.Add(scaleChoice);
        foreach (int seconds in new[] { 60, 600 }) {
            var button = Choice(seconds == 60 ? "60 s" : "10 min"); rangeButtons.Add(button);
            button.Click += (_, _) => { rangeSeconds = seconds; RefreshPerformance(); }; chartControls.Children.Add(button);
        }
        var reading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180 };
        resourceValue.CharacterSpacing = -24; reading.Children.Add(resourceValue);
        resourceUnit.VerticalAlignment = VerticalAlignment.Bottom; resourceUnit.Margin = new Thickness(0, 0, 0, 3); reading.Children.Add(resourceUnit);
        Grid.SetColumn(reading, 1); resourceHeading.Children.Add(reading); content.Children.Add(resourceHeading);
        foreach (var mode in new[] { "Celkem", "Logické", "Překrytí" }) {
            int index = cpuModes.Children.Count; var button = Choice(mode); button.Height = 25; cpuButtons.Add(button);
            button.Click += (_, _) => { cpuMode = index; RefreshPerformance(); }; cpuModes.Children.Add(button);
        }
        plotToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); plotToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        plotToolbar.ColumnDefinitions.Add(new ColumnDefinition()); plotToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        plotToolbar.Children.Add(cpuModes); Grid.SetColumn(chartControls, 1); plotToolbar.Children.Add(chartControls);
        content.Children.Add(plotToolbar); content.Children.Add(chart); content.Children.Add(processors); content.Children.Add(BuildGpu());
        retained.Margin = new Thickness(0, 7, 0, 0); content.Children.Add(retained);
        pageFileTitle.Margin = new Thickness(0, 18, 0, 8);
        content.Children.Add(pageFileTitle); content.Children.Add(pageFile);
        content.Children.Add(BuildPerformanceTimeline());
        var chartCard = Card(content); chartCard.VerticalAlignment = VerticalAlignment.Top; chartGrid.Children.Add(chartCard);
        var factCard = Card(facts); Grid.SetColumn(factCard, 1); chartGrid.Children.Add(factCard);
        performanceBody.Children.Add(chartGrid);
        Grid.SetRow(consumers, 1); performanceBody.Children.Add(consumers);
        page.Children.Add(performancePage);
    }

    private void BuildProcesses()
    {
        foreach (var height in new[] { new GridLength(108), new GridLength(51), new GridLength(1, GridUnitType.Star), new GridLength(92) })
            processPage.RowDefinitions.Add(new RowDefinition { Height = height });
        processPage.RowSpacing = 0;
        var system = Card(overview, new Thickness(23, 20, 23, 20)); processPage.Children.Add(system);
        var toolbar = new Grid();
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        foreach (var mode in new[] { ProcessMode.All, ProcessMode.Active, ProcessMode.Tree, ProcessMode.Applications }) {
            var button = Button(mode == ProcessMode.All ? "Všechny procesy" : mode == ProcessMode.Active ? "Aktivní" : mode == ProcessMode.Tree ? "Strom" : "Aplikace"); button.Height = 29;
            button.Click += (_, _) => SelectProcessMode(mode); filters.Children.Add(button);
        }
        filters.Children.Add(processCount); processCount.Margin = new Thickness(20, 8, 0, 0);
        toolbar.Children.Add(new ScrollViewer { Content = filters, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        processTable = new ProcessTable(processList, SortProcesses);
        var columns = Button("Sloupce"); columns.Height = 29; columns.HorizontalAlignment = HorizontalAlignment.Right; columns.VerticalAlignment = VerticalAlignment.Center;
        columns.Flyout = processTable.VisibilityMenu(); AutomationProperties.SetName(columns, "Zobrazit nebo skrýt sloupce procesů");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(columns); actions.Children.Add(exportSnapshot); actions.Children.Add(compareSnapshots); actions.Children.Add(processDetailsButton); actions.Children.Add(globalObjectSearchButton); actions.Children.Add(processEventsButton); actions.Children.Add(processDumpButton); actions.Children.Add(processActionsButton); toolbar.Children.Add(actions);
        Grid.SetColumn(actions, 1);
        Grid.SetRow(toolbar, 1); processPage.Children.Add(toolbar);
        var workspace = BuildApplicationGroups(processTable); Grid.SetRow(workspace, 2); processPage.Children.Add(workspace);
        var dock = new StackPanel { Padding = new Thickness(21, 18, 21, 18), Spacing = 12 };
        dock.Children.Add(selectionTitle); dock.Children.Add(selectionFacts); dock.Children.Add(applicationFacts);
        var selection = Card(new ScrollViewer { Content = dock, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetRow(selection, 3); processPage.Children.Add(selection);
        processPage.Visibility = Visibility.Collapsed; page.Children.Add(processPage);
    }

    private void ShowView(bool value) => ShowView(value ? DesktopView.Performance : DesktopView.Processes);
    private void ShowView(DesktopView next)
    {
        model.SetHeld(false); view = next;
        bool services = next == DesktopView.Services;
        bool hardware = next == DesktopView.Hardware;
        if (services) servicesModel ??= new ServiceModel();
        heading.Text = hardware ? "Sestava" : services ? "Služby" : performance ? "Výkon" : "Procesy";
        subtitle.Text = hardware ? "Procesor, paměť, základní deska a napájení." : services ? "Stavy, procesy a závislosti systému." : performance ? "Jeden pohled. Společná časová osa." : "Aplikace, prostředky a jejich souvislosti.";
        performancePage.Visibility = performance ? Visibility.Visible : Visibility.Collapsed;
        processPage.Visibility = next == DesktopView.Processes ? Visibility.Visible : Visibility.Collapsed;
        servicesPage.Visibility = services ? Visibility.Visible : Visibility.Collapsed;
        hardwarePage.Visibility = hardware ? Visibility.Visible : Visibility.Collapsed;
        search.Visibility = performance || hardware ? Visibility.Collapsed : Visibility.Visible;
        interval.Visibility = services || hardware ? Visibility.Collapsed : Visibility.Visible;
        search.PlaceholderText = services ? "Hledat službu, účet nebo PID" : "Hledat název nebo PID";
        AutomationProperties.SetName(search, search.PlaceholderText);
        search.Text = services ? servicesModel!.Query : processQuery;
        if (!services) model.SetQuery(processQuery);
        performanceNav.Background = Display.Brush(performance ? 0x3b3024u : 0x12181cu);
        processesNav.Background = Display.Brush(next == DesktopView.Processes ? 0x3b3024u : 0x12181cu);
        servicesNav.Background = Display.Brush(services ? 0x3b3024u : 0x12181cu);
        hardwareNav.Background = Display.Brush(hardware ? 0x3b3024u : 0x12181cu);
        servicesModel?.SetActivity(services && !IsIconic(windowHandle), model.Paused);
        if (services && serviceList.ItemsSource is null) serviceList.ItemsSource = servicesModel!.Visible;
        Resize();
        if (hardware) RefreshHardware(); else if (services) RefreshServices(); else if (performance) RefreshPerformance(); else RefreshProcesses();
    }

    private void Resize()
    {
        if (IsIconic(windowHandle)) return;
        if (root.ActualWidth <= 0) return;
        double width = root.ActualWidth;
        Grid.SetRow(chartControls, width <= 900 ? 1 : 0); Grid.SetColumn(chartControls, width <= 900 ? 0 : 1);
        Grid.SetColumnSpan(chartControls, width <= 900 ? 2 : 1);
        root.ColumnDefinitions[0].Width = new GridLength(width <= 1000 ? 68 : 88); sidebar.Width = width <= 1000 ? 68 : 88;
        double margin = width <= 1000 ? 20 : performance ? 25 : 30;
        page.Margin = new Thickness(margin, view == DesktopView.Services ? 1 : 0, margin, 22);
        performancePage.ColumnDefinitions[0].Width = new GridLength(width <= 780 ? 180 : width <= 1000 ? 225 : 278);
        var factCard = (FrameworkElement)chartGrid.Children[1];
        Grid.SetColumn(factCard, width <= 1250 ? 0 : 1); Grid.SetRow(factCard, width <= 1250 ? 1 : 0);
        chartGrid.ColumnDefinitions[1].Width = width <= 1250 ? new GridLength(0) : new GridLength(242);
        processPage.RowDefinitions[0].Height = root.ActualHeight < 650 ? new GridLength(0) : new GridLength(108);
        ResizeServices();
        ResizeHardware();
        ResizeApplicationGroups();
        if (performance) RefreshPerformance();
    }

    private void Tick()
    {
        if (sortBenchmarkRunning) return;
        long cpuTick = BeginLiveCpuTick();
        try {
            bool minimized = IsIconic(windowHandle);
            SetLiveCpuState(cpuTick, minimized);
            MarkLiveCpuModel(cpuTick, true);
            bool changed = model.Tick(!minimized && view != DesktopView.Services);
            MarkLiveCpuModel(cpuTick, false);
            if (!minimized) {
                bool canExport = !exporting && model.ProcessTime > 0;
                if (exportSnapshot.IsEnabled != canExport) exportSnapshot.IsEnabled = canExport;
            }
            TickServices(!minimized && view == DesktopView.Services);
            if (changed && !minimized) {
                if (selectedResource is null || !model.Resources.Contains(selectedResource)) resourceList.SelectedItem = model.Resources.FirstOrDefault();
                if (performance) RefreshPerformance(); else if (view == DesktopView.Processes) RefreshProcesses(); else if (view == DesktopView.Hardware) RefreshHardware();
            }
            if (++ticks % 10 == 0 && !minimized) {
                footer.Text = view == DesktopView.Services ? ServicesFooter() : (model.Paused ? "Pozastaveno" : "Živá data") + $" · {model.Processes.Count} procesů · stáří "
                    + Display.Number((NativeMonitor.MonotonicNowNs - model.ProcessTime) / 1_000_000_000d, " s")
                    + (model.ProcessError.Length == 0 ? "" : " · " + model.ProcessError) + (model.ResourceError.Length == 0 ? "" : " · " + model.ResourceError);
                if (model.HistoryWarning.Length != 0) footer.Text += " · " + model.HistoryWarning;
            }
            RunSmoke();
            if (App.SortBenchmarkPath is not null && ticks >= 20 && model.Processes.Count > 0) RunSortBenchmark();
        } catch (NativeMonitorException error) {
            if (smokePath is not null) { App.Trace(error.ToString()); throw; }
            footer.Text = error.Message;
        } catch (Exception error) when (smokePath is not null) { App.Trace(error.ToString()); throw; }
        finally { EndLiveCpuTick(cpuTick); }
    }

    private void HoldRows(object sender, PointerRoutedEventArgs args)
    {
        if (IsIconic(windowHandle)) return;
        var element = args.OriginalSource as DependencyObject;
        bool row = false;
        while (element is not null && element != processList) {
            if (element is ListViewItem item && item.Content is ProcessSlot) { row = true; break; }
            element = VisualTreeHelper.GetParent(element);
        }
        model.SetHeld(row);
    }

    private void RefreshProcesses()
    {
        RefreshApplicationGroups();
        processCount.Text = model.VisibleProcesses.Count + (model.Held ? " položek · pořadí přidrženo" : " položek");
        foreach (var resource in model.Resources.Where(row => row.Kind is ResourceKind.Cpu or ResourceKind.Memory)) {
            if (!overviewValues.TryGetValue(resource.Kind, out var value)) {
                value = (Label(resource.Title, 10, 0x95a4ad), Label(resource.Value, 20, Display.ResourceColor(resource.Kind)));
                var stack = new StackPanel { Spacing = 8 };
                stack.Children.Add(value.Caption); stack.Children.Add(value.Value);
                var button = Button(""); button.Content = stack; button.Height = 68; button.MinWidth = 180;
                var kind = resource.Kind;
                AutomationProperties.SetAutomationId(button, kind == ResourceKind.Cpu ? "process-overview-cpu" : "process-overview-memory");
                button.Click += (_, _) => { resourceList.SelectedItem = model.Resources.FirstOrDefault(row => row.Kind == kind); ShowView(true); };
                overview.Children.Add(button); overviewValues.Add(kind, value);
            }
            value.Caption.Text = resource.Title; value.Value.Text = resource.Value;
        }
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (updatingProcessSlots) return;
        UpdateProcessDetailsCommand();
        RefreshProcessActions();
        RefreshProcessDump();
        if (SelectedProcess is not { } row) { selectionTitle.Text = "Vyberte proces"; selectionFacts.Text = "Ctrl a Shift vyberou více řádků."; return; }
        selectionTitle.Text = row.Ended ? row.Name + " · ukončeno" : row.Name;
        selectionFacts.Text = $"PID {row.PidText} · {row.Threads} vláken · {row.Handles} popisovačů · CPU {row.CpuText} · {row.WorkingText}";
    }

    private void RunSmoke()
    {
        if (smokePath is null) return;
        if (smokeSnapshotExport is { IsCompleted: true }) smokeSnapshotExport.GetAwaiter().GetResult();
        bool ready = model.Processes.Any(row => row.Key.Pid == Environment.ProcessId && row.Key.Generation != 0)
            && model.Resources.Count > 2 && model.Resources.Any(row => row.Kind == ResourceKind.Cpu)
            && resourceList.ActualHeight > 0 && selectedResource is not null;
        if ((smokeMinimizedAt == 0 && ticks > 150) || (smokeMinimizedAt > 0 && ticks - smokeMinimizedAt > 200))
            throw new TimeoutException($"WinUI runtime check timed out: ready={ready};minimized={smokeMinimizedChecked};models={smokeModelsChecked}.");
        if (smokeMinimizedAt == 0 && ready && ticks >= 20) {
            verifyMinimized = WinUiChecks.BeginMinimizedCollection(model);
            smokeMinimizedAt = ticks;
            App.Trace("Minimizing window");
            ShowWindow(windowHandle, 6);
            return;
        }
        if (smokeMinimizedAt == 0) return;
        if (!smokeMinimizedChecked && ticks - smokeMinimizedAt >= 20) {
            verifyMinimized!(); smokeMinimizedChecked = true;
            App.Trace("Minimized collection passed; restoring window");
            ShowWindow(windowHandle, 9);
            return;
        }
        if (!smokeMinimizedChecked) return;
        if (ready && !smokeModelsChecked) {
            CheckHeldProcessRefresh();
            CheckHardwareRefresh();
            WinUiChecks.ContextMenus();
            WinUiChecks.ModelInvariants(model); WinUiChecks.ProcessNotifications(); smokeSnapshotExport = WinUiChecks.SnapshotExportChecks(SnapshotDocument.Capture(model)); smokeModelsChecked = true;
            smokeReadyAt = ticks;
            ShowView(false); var own = model.Processes.First(row => row.Key.Pid == Environment.ProcessId); SelectProcess(own, false);
            var overviewButtons = overview.Children.ToArray();
            RefreshProcesses();
            if (overviewButtons.Length != 2 || !overviewButtons.SequenceEqual(overview.Children))
                throw new InvalidOperationException("A process refresh recreated its overview controls.");
            smokeIcons = Task.WhenAll(Task.Run(() => ProcessIcons.Read(own.Key)), Task.Run(() => ProcessIcons.Read(own.Key with { Generation = own.Key.Generation + 1 })));
            smokeIconIsolation = Task.Run(() => WinUiChecks.IconWorkerIsolation(own.Key));
            smokeIconsAt = ticks;
        }
        int elapsed = ticks - smokeReadyAt;
        if (smokeModelsChecked && elapsed == 10) {
            if (!smokeIcons!.IsCompleted && ticks - smokeIconsAt < 100) { ++smokeReadyAt; return; }
            if (!smokeIcons!.IsCompletedSuccessfully || smokeIcons.Result[0] is not { Length: > 54 } || smokeIcons.Result[1] is not null)
                throw new InvalidOperationException("Process icons did not validate the process generation. " + IconWorkers.LastFailure, smokeIconIsolation?.Exception);
            smokeSharedIcon = WinUiChecks.SharedIconDecode(smokeIcons.Result[0]!);
            App.Trace("Process icons passed: own executable; recycled PID rejected");
            verifyProcessTable = WinUiChecks.BeginProcessTableReplay(processTable, processList);
        }
        if (smokeModelsChecked && elapsed == 20) {
            if (smokeIconIsolation is { IsCompleted: false } && ticks - smokeIconsAt < 100) { ++smokeReadyAt; return; }
            if (smokeIconIsolation is not { IsCompletedSuccessfully: true })
                throw new InvalidOperationException("Executable icon worker isolation checks failed.", smokeIconIsolation?.Exception);
            App.Trace("Icon worker isolation passed: timeout, restart, generation, shutdown");
            if (smokeSharedIcon is not { IsCompletedSuccessfully: true })
                throw new InvalidOperationException("Shared executable icon checks did not pass.", smokeSharedIcon?.Exception);
            verifyProcessTable!(); model.VisibleProcesses.RefreshView(); smokeColumnsChecked = true; resourceList.SelectedItem = model.Resources.First(row => row.Kind == ResourceKind.Memory); ShowView(true);
        }
        if (smokeModelsChecked && elapsed == 30) { CheckGraphRefresh(); resourceList.SelectedItem = model.Resources.First(row => row.Kind == ResourceKind.Cpu); cpuMode = 1; RefreshPerformance(); }
        if (smokeModelsChecked && elapsed == 40) {
            if (coreCharts.Count != model.Processors.Count || coreCharts.Values.Any(core => core.ActualWidth <= 0)) throw new InvalidOperationException("Logical CPU charts did not render.");
            cpuMode = 2; RefreshPerformance();
            CheckResourceViews();
            ShowView(DesktopView.Hardware); root.UpdateLayout();
            if (hardwareSections.Values.Any(section => section.Panel.Children.Count == 0) || hardwareCards.ActualWidth <= 0)
                throw new InvalidOperationException("Hardware overview did not render its facts.");
            App.Trace("Hardware overview passed: CPU, RAM, mainboard and UPS availability");
        }
        if (smokeModelsChecked && elapsed == 50) { ShowView(DesktopView.Services); smokeServicesStage = 1; }
        if (smokeServicesStage == 1 && servicesModel!.Time > 0 && servicesModel.Find("RpcSs") is not null) {
            UpdateServices(() => {
                WinUiChecks.Services(servicesModel);
                var candidate = servicesModel.All.FirstOrDefault(row => row.State == 4 && row.Name.StartsWith("WpnUserService_", StringComparison.OrdinalIgnoreCase));
                if (candidate is not null) servicesModel.Select(candidate.Name);
            });
            serviceList.ScrollIntoView(servicesModel.Selected); smokeServicesStage = 2;
        }
        if (smokeServicesStage == 2 && servicesModel!.Detail is { StatusError: 0 } detail && servicesModel.Selected?.Name == detail.Name) {
            if (serviceList.ActualWidth <= 0 || serviceList.ActualHeight <= 0 || serviceList.ActualHeight > root.ActualHeight) throw new InvalidOperationException("Service table did not lay out within the window.");
            int realized = Enumerable.Range(0, servicesModel.Visible.Count).Count(index => serviceList.ContainerFromIndex(index) is not null);
            if (servicesModel.Visible.Count > 100 && realized >= servicesModel.Visible.Count / 2) throw new InvalidOperationException("Service table materialized offscreen rows.");
            App.Trace($"Service virtualization passed: {realized}/{servicesModel.Visible.Count} realized rows");
            if (servicesModel.Host(model) is { } host) {
                model.SetQuery("no-matching-process"); processQuery = "no-matching-process";
                OpenServiceHost();
                if (SelectedProcess != host || !model.VisibleProcesses.Contains(host)) throw new InvalidOperationException("Service host navigation kept the old process filter.");
                App.Trace("Service host navigation passed: generation=" + host.Key.Generation);
                ShowView(DesktopView.Services);
            }
            dependenciesSelected = true; RefreshServiceDetail();
            smokeServiceTime = servicesModel.Time; smokeServicesAt = ticks; smokeServicesStage = 3; ShowWindow(windowHandle, 6);
        }
        if (smokeServicesStage == 3 && ticks - smokeServicesAt >= 20) {
            if (servicesModel!.Time != smokeServiceTime) throw new InvalidOperationException("Minimized service model continued updating its view; iconic=" + IsIconic(windowHandle) + ";ticks=" + (ticks - smokeServicesAt));
            smokeServicesStage = 4; ShowWindow(windowHandle, 9);
        }
        if (smokeServicesStage == 4 && servicesModel!.Time > smokeServiceTime && !servicesModel.Stale) {
            smokeServicesChecked = true;
            App.Trace($"Services passed: count={servicesModel.All.Count};detail={servicesModel.Detail?.Name};resumed=True");
        }
        if (smokeServicesChecked && smokeSnapshotExport is { IsCompletedSuccessfully: true }) {
            if (smokeSnapshotComparison is null) { smokeSnapshotComparison = SnapshotComparisonUiChecks.RunAsync(); return; }
            if (!smokeSnapshotComparison.IsCompleted) return;
            smokeSnapshotComparison.GetAwaiter().GetResult();
            App.Trace($"aot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported};ready={smokeModelsChecked};models={smokeModelsChecked};minimized={smokeMinimizedChecked};services={smokeServicesChecked};columns={smokeColumnsChecked};export=True;comparison=True;processes={model.Processes.Count};resources={model.Resources.Count};charts={coreCharts.Count}");
            RequestMainClose();
        }
    }
}
