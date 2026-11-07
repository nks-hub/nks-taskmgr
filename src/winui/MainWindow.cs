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
internal enum DesktopView { Processes, Performance, Services, Hardware, Applications, Users, Startup }

public sealed partial class MainWindow : Window
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
    private readonly nint windowHandle;
    private readonly MonitorModel model = new();
    private readonly Grid root = new() { Background = Display.Brush(0x171d21) };
    private readonly Grid page = new();
    private readonly Grid sidebar = new() { Background = Display.Brush(0x12181c), Width = 68 };
    private double sidebarWidth = 68;
    private readonly TextBlock heading = Label(Loc.T("Výkon"), 16, 0xe9eef0);
    private readonly Grid shellHeader = new();
    private readonly TextBlock footer = Label(Loc.T("Načítám živá data…"), 9, 0x95a4ad);
    private readonly TextBox search = new() { Width = 220, Height = 30, MinHeight = 0, FontSize = 12, PlaceholderText = Loc.T("Hledat název nebo PID"), Visibility = Visibility.Collapsed };
    private readonly Button pause = Button(Loc.T("Pozastavit")), interval = Button("1 s");
    private readonly Button performanceNav = Button(Loc.T("Výkon")), processesNav = Button(Loc.T("Procesy")), servicesNav = Button(Loc.T("Služby"));
    private readonly Button hardwareNav = Button(Loc.T("Sestava")), applicationNav = Button(Loc.T("Aplikace"));
    private readonly List<(ProcessMode Mode, Button Button)> processModeButtons = [];
    private readonly ComboBox processValueMode = new() { Width = 168, Height = 28, MinHeight = 0, FontSize = 11,
        Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ListView resourceList = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly ListView processList = new() { SelectionMode = ListViewSelectionMode.Extended, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly Grid performancePage = new(), processPage = new();
    private ProcessTable processTable = null!;
    private readonly Grid performanceBody = new(), chartGrid = new();
    private readonly TextBlock resourceCount = Label("", 9, 0x95a4ad);
    private readonly TextBlock resourceEyebrow = Label(Loc.T("PROCESOR"), 9, 0x95a4ad);
    private readonly TextBlock resourceTitle = Label(Loc.T("Procesor"), 20, 0xe9eef0), resourceDescription = Label("", 10, 0x95a4ad);
    private readonly TextBlock resourceValue = Label(Loc.T("Načítám…"), 42, 0x80c5c0);
    private readonly TextBlock resourceUnit = Label("", 13, 0x95a4ad);
    private readonly List<ToggleButton> rangeButtons = [], cpuButtons = [];
    private readonly StackPanel cpuModes = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
    private readonly ToggleButton scaleChoice = Choice(Loc.T("Auto měřítko"));
    private readonly Grid plotToolbar = new() { Margin = new Thickness(0, 12, 0, 12), RowSpacing = 6 };
    private readonly StackPanel chartControls = new() { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Grid processors = new();
    private readonly ChartView chart = new() { Height = 260 }, pageFile = new() { Height = 94 };
    private readonly TextBlock pageFileTitle = Label("", 10, 0x95a4ad);
    private readonly StackPanel facts = new() { Spacing = 13, Padding = new Thickness(22) };
    private readonly StackPanel consumers = new() { Spacing = 9 };
    private readonly TextBlock processCount = Label("", 10, 0x95a4ad);
    private readonly StackPanel overview = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ScrollViewer overviewScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0) };
    private readonly Dictionary<ResourceKind, (Button Action, TextBlock Caption, TextBlock Value)> overviewValues = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    private ResourceRow? selectedResource;
    private DesktopView view = DesktopView.Performance;
    private bool performance => view == DesktopView.Performance;
    private string processQuery = "";
    private string? navigationSearchText;
    private int cpuMode, rangeSeconds = 60, ticks;
    private string? smokePath;
    private bool smokeModelsChecked;
    private bool smokeMinimizedChecked;
    private Action? verifyMinimized;
    private Action? verifyHiddenProcesses;
    private Action? verifyProcessTable;
    private bool smokeColumnsChecked;
    private Task<byte[]?[]>? smokeIcons;
    private Task? smokeSharedIcon;
    private Task? smokeIconIsolation;
    private Task? smokeResourceContext;
    private Task? smokeTableLifecycle;
    private Task? smokeBranchValues;
    private bool processBranchCheckRunning;
    private int smokeIconsAt;
    private int smokeMinimizedAt, smokeReadyAt, smokeHiddenAt;
    private int smokeServicesStage, smokeServicesAt;
    private long smokeServiceTime;
    private bool smokeServicesChecked;

    public MainWindow()
    {
        App.Trace("Window objects created");
        windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (App.SmokePath is not null) App.Trace("Presenter=" + AppWindow.Presenter.GetType().FullName + ";overlapped=" + (AppWindow.Presenter is OverlappedPresenter));
        Title = "NKS Task Manager";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        Application.Current.Resources["ContentControlThemeFontFamily"] = UiTypography.Font;
        Application.Current.Resources["TextControlThemeFontFamily"] = UiTypography.Font;
        ConfigureResources(); ConfigureSnapshotExport(); ConfigureSnapshotComparison(); ConfigureProcessDetails(); ConfigureProcessActions(); ConfigureProcessDump(); ConfigureProcessEvents(); ConfigureGlobalObjectSearch(); BuildShell(); ConfigureAppearance(); ConfigureUpdates(); BuildPerformance(); BuildProcesses(); BuildServices(); BuildHardware(); BuildUsers(); BuildStartup(); InitializeContextMenus();
        // Wide enough for the sidebar, page margins and the process columns without horizontal scrolling.
        AppWindow.ResizeDip((int)Math.Ceiling(sidebarWidth + 32 + processTable.Columns.MinimumWidth), 760);
        App.Trace("Window layout created");
        Content = root;
        App.Trace("Content assigned");
        resourceList.ItemsSource = model.Resources;
        App.Trace("Resource source assigned");
        // The default cache realizes four extra viewports of rows; every per-second text change in them costs layout.
        // NativeAOT exposes the panel through its base projection, so the value is set without a cast.
        processList.Loaded += (_, _) => processList.ItemsPanelRoot?.SetValue(ItemsStackPanel.CacheLengthProperty, 1d);
        processList.ItemsSource = processSlots;
        processList.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
        PreserveProcessView(model);
        App.Trace("Process source assigned");
        var resourceTemplate = Application.Current.Resources["ResourceTemplate"];
        App.Trace("Resource template: " + resourceTemplate.GetType().FullName);
        resourceList.ItemTemplate = resourceTemplate.As<DataTemplate>();
        processList.ItemTemplate = Application.Current.Resources["ProcessTemplate"].As<DataTemplate>();
        App.Trace("Templates assigned");
        ListAccessibility.NameResourceItems(resourceList);
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
        pause.Click += (_, _) => { model.SetPaused(!model.Paused); pause.Content = model.Paused ? Loc.T("Pokračovat") : Loc.T("Pozastavit"); };
        interval.Click += (_, _) => { model.SetInterval(model.Interval == 1000 ? 250u : 1000u); interval.Content = model.Interval == 1000 ? "1 s" : "250 ms"; };
        search.TextChanged += (_, _) => {
            if (navigationSearchText is { } expected) {
                navigationSearchText = null;
                if (search.Text == expected) return;
            }
            if (view == DesktopView.Services) UpdateServices(() => servicesModel!.SetQuery(search.Text));
            else { processQuery = search.Text; model.SetQuery(search.Text); }
        };
        processesNav.Click += (_, _) => { ShowApplications(false); ShowView(DesktopView.Processes); };
        applicationNav.Click += (_, _) => { ShowApplications(true); ShowView(DesktopView.Applications); };
        performanceNav.Click += (_, _) => ShowView(true);
        servicesNav.Click += (_, _) => ShowView(DesktopView.Services);
        hardwareNav.Click += (_, _) => ShowView(DesktopView.Hardware);
        root.PreviewKeyDown += (_, args) => {
            if (args.Handled || args.Key != VirtualKey.F) return;
            static bool IsDown(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
            if (!IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift) || IsDown(VirtualKey.Menu)
                || IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) return;
            if (view is DesktopView.Performance or DesktopView.Hardware or DesktopView.Startup) ShowView(false);
            search.Focus(FocusState.Keyboard);
            args.Handled = true;
        };
        AutomationProperties.SetAcceleratorKey(search, "Ctrl+F");
        ToolTipService.SetToolTip(search, Loc.T("Hledat (Ctrl+F)"));
        root.SizeChanged += (_, _) => Resize();
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(VisibleTickMs);
        timer.Tick += (_, _) => Tick(); timer.Start();
        Closed += (_, _) => { timer.Stop(); IconWorkers.Stop(); servicesModel?.Dispose(); model.Dispose(); };
        AppWindow.Changed += (_, _) => { Resize(); PaceTimer(); };
        smokePath = App.SmokePath;
        ConfigureGapDiagnostics();
        ShowView(LaunchArguments.Parse(LaunchArguments.Current).View ?? DesktopView.Performance);
        // Initial focus lands on the first navigation button with a keyboard focus rectangle even when another view is open;
        // it moves to the active view without the rectangle, which returns with the first Tab.
        RoutedEventHandler? initialFocus = null;
        initialFocus = (_, _) => {
            processesNav.GotFocus -= initialFocus;
            var current = view switch {
                DesktopView.Performance => performanceNav, DesktopView.Applications => applicationNav, DesktopView.Services => servicesNav,
                DesktopView.Users => usersNav, DesktopView.Hardware => hardwareNav, DesktopView.Startup => startupNav, _ => processesNav
            };
            DispatcherQueue.TryEnqueue(() => current.Focus(FocusState.Pointer));
        };
        processesNav.GotFocus += initialFocus;
        // Only the startup focus is redirected; a later Tab or click onto Procesy must keep its focus.
        var focusWindow = DispatcherQueue.CreateTimer(); focusWindow.Interval = TimeSpan.FromSeconds(3); focusWindow.IsRepeating = false;
        focusWindow.Tick += (_, _) => processesNav.GotFocus -= initialFocus; focusWindow.Start();
        App.Trace("Window initialized");
    }

    // Selected labels use HighlightText in high contrast so they stay readable on the Highlight fill.
    private static void MarkNavigation(Button button, bool selected)
    {
        button.Background = Display.Brush(selected ? 0x28343bu : 0x12181cu);
        // A view switch can leave the previously clicked button in its PointerOver/Pressed look; reset it so only the active view stands out.
        VisualStateManager.GoToState(button, "Normal", false);
        var brush = Display.Brush(selected ? ThemePalette.SelectedLabel : 0x95a4adu);
        if (button.Content is not Panel content) return;
        foreach (var child in content.Children) {
            if (child is TextBlock text) text.Foreground = brush;
            else if (child is IconElement icon) icon.Foreground = brush;
            else if (child is Viewbox { Child: Panel canvas }) foreach (var path in canvas.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>()) path.Stroke = brush;
        }
    }

    /// <summary>Formats a count with the plural form the UI language uses for it; the forms are translated "{0} …" texts.</summary>
    internal static string CountText(int count, string one, string few, string many)
        => string.Format(Loc.Culture, PluralForm(count, one, few, many), count);

    private static TextBlock Label(string text, double size, uint color) => new() {
        Text = text, FontSize = Math.Max(11, size), Foreground = Display.Brush(color), FontFamily = UiTypography.Font,
        FontWeight = size >= 14 && size != 42 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private static Button Button(string text) => new() {
        Content = text, MinHeight = 0, Height = 30, FontSize = 12, FontFamily = UiTypography.Font, Padding = new Thickness(10, 0, 10, 0),
        Background = Display.Brush(0x1d252a), BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4)
    };
    private static Border Card(UIElement child, Thickness padding = default) => new() {
        Child = child, Padding = padding, Background = Display.Brush(0x171d21), BorderBrush = Display.Brush(0x2c363d),
        BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0)
    };
    private static ToggleButton Choice(string text) => new() {
        Content = text, MinHeight = 0, Height = 27, FontSize = 11, FontFamily = UiTypography.Font, Padding = new Thickness(12, 0, 12, 0),
        Background = Display.Brush(0x1d252a), BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4)
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
        itemStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, ProcessTable.RowHeight));
        processList.ItemContainerStyle = itemStyle;
        var resourceStyle = new Style { TargetType = typeof(ListViewItem), BasedOn = itemStyle };
        resourceStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 50d));
        resourceList.ItemContainerStyle = resourceStyle;
        AutomationProperties.SetName(resourceList, Loc.T("Jednotlivé prostředky systému"));
        AutomationProperties.SetName(processList, Loc.T("Živé procesy"));
        AutomationProperties.SetAutomationId(processList, "processes-list");
        AutomationProperties.SetName(search, Loc.T("Hledat název procesu nebo PID"));
        search.FontFamily = UiTypography.Font; search.FontSize = 12;
    }

    private void BuildShell()
    {
        foreach (var height in new[] { new GridLength(32), new GridLength(36), new GridLength(1, GridUnitType.Star), new GridLength(22) }) root.RowDefinitions.Add(new RowDefinition { Height = height });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) }); root.ColumnDefinitions.Add(new ColumnDefinition());
        var title = new Grid { Background = Display.Brush(0x12181c), BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(0, 0, 0, 1) };
        var titleIcon = new Image { Width = 16, Height = 16, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "app.png"))), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 0, 0, 0) };
        title.Children.Add(titleIcon);
        var titleText = Label("NKS Task Manager", 12, 0xe9eef0);
        titleText.HorizontalAlignment = HorizontalAlignment.Left; titleText.VerticalAlignment = VerticalAlignment.Center;
        titleText.Margin = new Thickness(42, 0, 0, 0); title.Children.Add(titleText);
        Grid.SetColumnSpan(title, 2); root.Children.Add(title); SetTitleBar(title);
        var nav = new StackPanel { Spacing = 4, Margin = new Thickness(6, 8, 6, 0) };
        foreach (var button in new[] { processesNav, applicationNav, performanceNav, servicesNav, usersNav, hardwareNav, startupNav }) {
            button.Height = 44; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.FontSize = 11; button.BorderThickness = new Thickness(0);
            button.Padding = new Thickness(4, 0, 4, 0);
            var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            string name = button == startupNav ? Loc.T("Po spuštění") : button == usersNav ? Loc.T("Uživatelé") : button == applicationNav ? Loc.T("Aplikace") : button == performanceNav ? Loc.T("Výkon") : button == servicesNav ? Loc.T("Služby") : button == hardwareNav ? Loc.T("Sestava") : Loc.T("Procesy");
            AutomationProperties.SetName(button, name);
            content.Children.Add(button == startupNav ? new SymbolIcon(Symbol.Play) { Width = 20, Height = 20, Foreground = Display.Brush(0x95a4ad) }
                : button == applicationNav || button == usersNav ? new SymbolIcon(button == usersNav ? Symbol.People : Symbol.AllApps) { Width = 20, Height = 20, Foreground = Display.Brush(0x95a4ad) }
                : UiIcon.Create(button == performanceNav ? "performance" : button == servicesNav ? "services" : button == hardwareNav ? "hardware" : "processes", 0x95a4ad));
            var label = Label(button == startupNav ? Loc.T("Spuštění") : name, 9, 0x95a4ad);
            content.Children.Add(label);
            // 68 DIP leaves 48 DIP for the label (6 + 4 DIP margins and padding each side); longer translations widen the sidebar.
            label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            sidebarWidth = Math.Min(120, Math.Max(sidebarWidth, Math.Ceiling(label.DesiredSize.Width) + 20));
            button.Content = content;
            nav.Children.Add(button);
        }
        sidebar.Children.Add(new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetRow(sidebar, 1); Grid.SetRowSpan(sidebar, 3); root.Children.Add(sidebar);
        shellHeader.Padding = new Thickness(16, 0, 16, 0);
        shellHeader.ColumnDefinitions.Add(new ColumnDefinition()); shellHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var context = new Grid(); context.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); context.ColumnDefinitions.Add(new ColumnDefinition());
        heading.VerticalAlignment = VerticalAlignment.Center; context.Children.Add(heading);
        overviewScroll.Content = overview; Grid.SetColumn(overviewScroll, 1); context.Children.Add(overviewScroll); shellHeader.Children.Add(context);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(search); tools.Children.Add(interval); tools.Children.Add(pause); Grid.SetColumn(tools, 1); shellHeader.Children.Add(tools);
        Grid.SetRow(shellHeader, 1); Grid.SetColumn(shellHeader, 1); root.Children.Add(shellHeader);
        page.Margin = new Thickness(16, 0, 16, 8); Grid.SetRow(page, 2); Grid.SetColumn(page, 1); root.Children.Add(page);
        var status = new Border { BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 3, 10, 0), Child = footer };
        Grid.SetRow(status, 3); Grid.SetColumn(status, 1); root.Children.Add(status);
    }

    private void BuildProcesses()
    {
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            processPage.RowDefinitions.Add(new RowDefinition { Height = height });
        processPage.RowSpacing = 0;
        var toolbar = new Grid { MinHeight = 34 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        foreach (var mode in new[] { ProcessMode.All, ProcessMode.Active, ProcessMode.Tree }) {
            var button = Button(mode == ProcessMode.All ? Loc.T("Všechny procesy") : mode == ProcessMode.Active ? Loc.T("Aktivní") : Loc.T("Strom")); button.Height = 28;
            button.Click += (_, _) => SelectProcessMode(mode); filters.Children.Add(button); processModeButtons.Add((mode, button));
        }
        processValueMode.Items.Add(Loc.T("Vlastní hodnoty")); processValueMode.Items.Add(Loc.T("Proces s potomky"));
        processValueMode.SelectedIndex = 0;
        processValueMode.SelectionChanged += (_, _) => {
            if (processValueMode.SelectedIndex < 0) return;
            var value = processValueMode.SelectedIndex == 1 ? ProcessTableValueMode.ProcessWithDescendants : ProcessTableValueMode.Own;
            if (model.TableValueMode == value) return;
            model.SetTableValueMode(value); RefreshProcesses();
        };
        AutomationProperties.SetName(processValueMode, Loc.T("Význam hodnot ve stromu procesů"));
        AutomationProperties.SetAutomationId(processValueMode, "process-tree-value-mode");
        ToolTipService.SetToolTip(processValueMode, Loc.T("Vlastní hodnoty patří jednomu PID. Proces s potomky sčítá CPU, paměť a I/O ověřených členů celé větve i při hledání nebo sbalení. Chybějící údaje jsou označené. Součet pracovních sad může sdílené stránky RAM započítat opakovaně."));
        filters.Children.Add(processValueMode);
        filters.Children.Add(processCount); processCount.Margin = new Thickness(8, 0, 0, 0); processCount.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(new ScrollViewer { Content = filters, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        processTable = new ProcessTable(processList, SortProcesses);
        var columns = Button(Loc.T("Sloupce")); columns.Height = 28; columns.HorizontalAlignment = HorizontalAlignment.Right; columns.VerticalAlignment = VerticalAlignment.Center;
        columns.Flyout = processTable.VisibilityMenu(); AutomationProperties.SetName(columns, Loc.T("Zobrazit nebo skrýt sloupce procesů"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in new[] { columns, exportSnapshot, compareSnapshots, processDetailsButton, globalObjectSearchButton, processEventsButton, processDumpButton, processActionsButton }) {
            action.Height = 28; action.MinHeight = 0; actions.Children.Add(action);
        }
        // Icon-only commands get a visible word when the window is wide enough; narrow windows keep the icons alone.
        var labels = new List<TextBlock>();
        foreach (var (button, label) in new[] { (exportSnapshot, Loc.T("Export")), (compareSnapshots, Loc.T("Porovnat")), (processDetailsButton, Loc.T("Podrobnosti")),
                     (globalObjectSearchButton, Loc.T("Hledat objekty")), (processEventsButton, Loc.T("Události")) }) {
            var text = new TextBlock { Text = label, FontSize = 12, FontFamily = UiTypography.Font, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add((UIElement)button.Content); content.Children.Add(text);
            button.Content = content; button.Width = double.NaN; button.MinWidth = 35; labels.Add(text);
        }
        void LabelActions() {
            bool wide = root.ActualWidth >= 1280;
            foreach (var label in labels) label.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            foreach (var button in new[] { exportSnapshot, compareSnapshots, processDetailsButton, globalObjectSearchButton, processEventsButton })
                button.Padding = wide ? new Thickness(8, 0, 10, 0) : new Thickness(0);
        }
        root.SizeChanged += (_, _) => LabelActions(); LabelActions();
        toolbar.Children.Add(actions);
        Grid.SetColumn(actions, 1);
        processPage.Children.Add(toolbar);
        var workspace = BuildApplicationGroups(processTable); filters.Children.Insert(0, applicationToolbar);
        Grid.SetRow(workspace, 1); processPage.Children.Add(workspace);
        processPage.Visibility = Visibility.Collapsed; page.Children.Add(processPage);
    }

    internal void ActivateExisting()
    {
        if (IsIconic(windowHandle)) ShowWindow(windowHandle, 9);
        Activate();
        SetForegroundWindow(windowHandle);
    }

    private void ShowView(bool value) => ShowView(value ? DesktopView.Performance : DesktopView.Processes);
    private void ShowView(DesktopView next)
    {
        pendingReveal = null;
        if (view == DesktopView.Processes && model.Mode != ProcessMode.Applications && next != DesktopView.Processes && next != DesktopView.Applications) {
            var state = CaptureProcessNavigation(); CancelNavigationRestore();
            processNavigation = pendingNavigationRestore = state;
        }
        if (next == DesktopView.Applications && model.Mode != ProcessMode.Applications) ShowApplications(true);
        else if (next != DesktopView.Applications && model.Mode == ProcessMode.Applications) ShowApplications(false);
        model.SetHeld(false); view = next;
        bool services = next == DesktopView.Services;
        bool hardware = next == DesktopView.Hardware;
        bool applications = next == DesktopView.Applications;
        bool startup = next == DesktopView.Startup;
        if (services) servicesModel ??= new ServiceModel();
        heading.Text = startup ? Loc.T("Po spuštění") : hardware ? Loc.T("Sestava") : services ? Loc.T("Služby") : performance ? Loc.T("Výkon") : applications ? Loc.T("Aplikace") : Loc.T("Procesy");
        heading.Visibility = services || hardware || startup ? Visibility.Visible : Visibility.Collapsed;
        model.SetResourceViewVisible(performance);
        performancePage.Visibility = performance ? Visibility.Visible : Visibility.Collapsed;
        processPage.Visibility = next is DesktopView.Processes or DesktopView.Applications ? Visibility.Visible : Visibility.Collapsed;
        model.SetProcessViewVisible(processPage.Visibility == Visibility.Visible);
        foreach (var item in processModeButtons) item.Button.Visibility = applications ? Visibility.Collapsed : Visibility.Visible;
        servicesPage.Visibility = services ? Visibility.Visible : Visibility.Collapsed;
        hardwarePage.Visibility = hardware ? Visibility.Visible : Visibility.Collapsed;
        search.Visibility = performance || hardware || startup ? Visibility.Collapsed : Visibility.Visible;
        overviewScroll.Visibility = next == DesktopView.Processes ? Visibility.Visible : Visibility.Collapsed;
        interval.Visibility = services || hardware || startup ? Visibility.Collapsed : Visibility.Visible;
        search.PlaceholderText = services ? Loc.T("Hledat službu, účet nebo PID") : Loc.T("Hledat název nebo PID");
        AutomationProperties.SetName(search, search.PlaceholderText);
        SetNavigationSearchText(services ? servicesModel!.Query : processQuery);
        if (!services) model.SetQuery(processQuery);
        MarkNavigation(performanceNav, performance);
        MarkNavigation(processesNav, next == DesktopView.Processes);
        MarkNavigation(applicationNav, applications);
        MarkNavigation(servicesNav, services);
        MarkNavigation(hardwareNav, hardware);
        ShowUsers(next == DesktopView.Users);
        ShowStartupPage(startup);
        servicesModel?.SetActivity(services && !IsIconic(windowHandle), model.Paused);
        if (services && serviceList.ItemsSource is null) serviceList.ItemsSource = servicesModel!.Visible;
        Resize();
        if (hardware) RefreshHardware(); else if (services) RefreshServices(); else if (performance) RefreshPerformance(); else if (!startup) RefreshProcesses();
        processTable.SetSort(model.SortColumn, model.SortDescending);
        ResumeProcessNavigation();
    }

    private void Resize()
    {
        if (IsIconic(windowHandle)) return;
        if (root.ActualWidth <= 0) return;
        double width = root.ActualWidth;
        root.ColumnDefinitions[0].Width = new GridLength(sidebarWidth); sidebar.Width = sidebarWidth;
        ResizeSidebarActions(true);
        shellHeader.Padding = new Thickness(16, 0, 16, 0);
        page.Margin = new Thickness(16, 0, 16, 8);
        ResizePerformanceLayout(width);
        ResizeServices();
        ResizeHardware();
        ResizeApplicationGroups();
        if (performance) RefreshPerformance();
    }

    private void PaceTimer()
    {
        // Deferred: ShowWindow inside Tick can raise AppWindow.Changed synchronously.
        if (PaceTimer(IsIconic(windowHandle))) DispatcherQueue.TryEnqueue(Tick);
    }

    // Minimized ticks only drain the newest-frame mailbox and history; half the sampling interval still reads
    // every frame, and a frame overwritten before it is read is reported as a gap (F127). In the background full process
    // frames arrive about every 5 s and history has its own bounded stream, so half of that period still reads every frame.
    // Visible, the poll also follows half the sampling interval (at most 250 ms): at 1 s the UI thread wakes 4 instead of
    // 10 times a second, and every frame is still read.
    private const uint VisibleTickMs = 250;
    private long nextSecondTick;
    private bool secondTick;

    private bool PaceTimer(bool minimized)
    {
        model.SetBackground(minimized);
        var wanted = TimeSpan.FromMilliseconds(minimized ? Math.Max(100u, model.Background ? 2500u : model.Interval / 2) : Math.Clamp(model.Interval / 2, 100u, VisibleTickMs));
        if (timer.Interval == wanted) return false;
        timer.Interval = wanted;
        return !minimized;
    }

    private void Tick()
    {
        if (sortBenchmarkRunning || applicationTemplateCheckRunning || processBranchCheckRunning) return;
        long cpuTick = BeginLiveCpuTick();
        try {
            bool minimized = IsIconic(windowHandle);
            PaceTimer(minimized);
            // Once-a-second texts (footer, service ages) follow wall time, not a tick count, because the tick period varies.
            long nowMs = Environment.TickCount64; secondTick = nowMs >= nextSecondTick;
            if (secondTick) nextSecondTick = nowMs + 1000;
            SetLiveCpuState(cpuTick, minimized);
            MarkLiveCpuModel(cpuTick, true);
            bool changed = model.Tick(!minimized && view != DesktopView.Services);
            MarkLiveCpuModel(cpuTick, false);
            if (!minimized) {
                bool canExport = !exporting && model.ProcessTime > 0;
                if (exportSnapshot.IsEnabled != canExport) exportSnapshot.IsEnabled = canExport;
                RefreshApplicationInventory();
            }
            TickServices(!minimized && view == DesktopView.Services);
            if (changed && !minimized) {
                if (selectedResource is null || !model.Resources.Contains(selectedResource)) resourceList.SelectedItem = model.Resources.FirstOrDefault();
                if (performance) RefreshPerformance(); else if (view is DesktopView.Processes or DesktopView.Applications) RefreshProcesses(); else if (view == DesktopView.Hardware) RefreshHardware(); else if (view == DesktopView.Users) RefreshUsers();
            }
            ++ticks;
            if (secondTick && !minimized) {
                footer.Text = view == DesktopView.Services ? ServicesFooter() : Loc.F("{0} · {1} procesů · stáří {2}", model.Paused ? Loc.T("Pozastaveno") : Loc.T("Živá data"),
                    model.Processes.Count, Display.Number((NativeMonitor.MonotonicNowNs - model.ProcessTime) / 1_000_000_000d, " s"))
                    + (model.ProcessError.Length == 0 ? "" : " · " + model.ProcessError) + (model.ResourceError.Length == 0 ? "" : " · " + model.ResourceError);
                if (model.HistoryWarning.Length != 0) footer.Text += " · " + model.HistoryWarning;
                ToolTipService.SetToolTip(footer, Loc.T("Doba sběru") + " " + Display.Number(model.CollectionMs, " ms"));
            }
            RunSmoke();
            if (App.SortBenchmarkPath is not null && ticks >= 20 && model.Processes.Count > 0) RunSortBenchmark();
        } catch (NativeMonitorException error) {
            RecordGapDiagnosticError(error);
            if (smokePath is not null) { App.Trace(error.ToString()); throw; }
            footer.Text = error.Message;
        } catch (Exception error) when (smokePath is not null) { App.Trace(error.ToString()); throw; }
        finally { RecordGapDiagnosticTick(); EndLiveCpuTick(cpuTick); }
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
        bool treeValues = model.Mode == ProcessMode.Tree;
        var valueModeVisibility = treeValues ? Visibility.Visible : Visibility.Collapsed;
        if (processValueMode.Visibility != valueModeVisibility) processValueMode.Visibility = valueModeVisibility;
        int selectedValue = model.TableValueMode == ProcessTableValueMode.ProcessWithDescendants ? 1 : 0;
        if (processValueMode.SelectedIndex != selectedValue) processValueMode.SelectedIndex = selectedValue;
        processTable.SetBranchValues(treeValues && selectedValue == 1);
        foreach (var item in processModeButtons) {
            var background = item.Mode == model.Mode ? choiceSelected : choiceIdle;
            var foreground = item.Mode == model.Mode ? performanceInk : performanceMuted;
            if (!ReferenceEquals(item.Button.Background, background)) item.Button.Background = background;
            if (!ReferenceEquals(item.Button.Foreground, foreground)) item.Button.Foreground = foreground;
        }
        RefreshApplicationGroups();
        int visible = model.VisibleProcesses.Count;
        string countText = CountText(visible, Loc.T("{0} proces"), Loc.T("{0} procesy"), Loc.T("{0} procesů"));
        if (model.Held) countText = Loc.F("{0} · pořadí přidrženo", countText);
        if (processCount.Text != countText) processCount.Text = countText;
        RefreshProcessOverview();
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (updatingProcessSlots) return;
        UpdateProcessDetailsCommand();
        RefreshProcessActions();
        RefreshProcessDump();
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
            if (verifyHiddenProcesses is null) {
                ShowView(DesktopView.Performance);
                verifyHiddenProcesses = WinUiChecks.BeginMinimizedCollection(model, resourceViewHidden: false);
                smokeHiddenAt = ticks;
                return;
            }
            if (ticks - smokeHiddenAt < 20) return;
            verifyHiddenProcesses();
            App.Trace("Hidden process collection passed: fresh data and CPU history, no process UI notifications");
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
        if (ready && smokeResourceContext is null) {
            ShowView(DesktopView.Performance);
            smokeResourceContext = CheckResourceContextAndPreviewAsync();
            return;
        }
        if (smokeResourceContext is { IsCompleted: false }) return;
        smokeResourceContext?.GetAwaiter().GetResult();
        if (ready && !smokeModelsChecked) {
            if (smokeBranchValues is null) { smokeBranchValues = CheckProcessBranchUiAsync(); return; }
            if (!smokeBranchValues.IsCompleted) return;
            smokeBranchValues.GetAwaiter().GetResult();
            CheckHeldProcessRefresh();
            CheckHardwareRefresh();
            WinUiChecks.ContextMenus(); LaunchArguments.Check();
            WinUiChecks.ModelInvariants(model); WinUiChecks.ProcessNotifications(); WinUiChecks.LocalizationChecks(); smokeSnapshotExport = WinUiChecks.SnapshotExportChecks(SnapshotDocument.Capture(model)); smokeModelsChecked = true;
            smokeReadyAt = ticks;
            ShowView(false); var own = model.Processes.First(row => row.Key.Pid == Environment.ProcessId); SelectProcess(own, false);
            var overviewButtons = overview.Children.ToArray();
            RefreshProcesses();
            if (overviewButtons.Length != 4 || !overviewButtons.SequenceEqual(overview.Children))
                throw new InvalidOperationException("A process refresh recreated its overview controls.");
            smokeIcons = Task.WhenAll(Task.Run(() => ProcessIcons.Read(own.Key)), Task.Run(() => ProcessIcons.Read(own.Key with { Generation = own.Key.Generation + 1 })));
            smokeIconIsolation = Task.Run(() => WinUiChecks.IconWorkerIsolation(own.Key));
            smokeIconsAt = ticks;
        }
        int elapsed = ticks - smokeReadyAt;
        if (smokeModelsChecked && elapsed == 10) {
            if (smokeTableLifecycle is null) {
                smokeTableLifecycle = WinUiChecks.ProcessTableLifecycleAsync(this, processTable, processList);
                ++smokeReadyAt; return;
            }
            if (!smokeTableLifecycle.IsCompleted) { ++smokeReadyAt; return; }
            smokeTableLifecycle.GetAwaiter().GetResult();
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
            ListAccessibility.RequireItemNames(processList, "Process list");
            verifyProcessTable!(); model.VisibleProcesses.RefreshView(); smokeColumnsChecked = true; resourceList.SelectedItem = model.Resources.First(row => row.Kind == ResourceKind.Memory); ShowView(true);
        }
        if (smokeModelsChecked && elapsed == 30) { CheckGraphRefresh(); resourceList.SelectedItem = model.Resources.First(row => row.Kind == ResourceKind.Cpu); cpuMode = 1; RefreshPerformance(); }
        if (smokeModelsChecked && elapsed == 40) {
            if (coreCharts.Count != model.Processors.Count || coreCharts.Values.Any(core => core.ActualWidth <= 0)) throw new InvalidOperationException("Logical CPU charts did not render.");
            cpuMode = 2; RefreshPerformance();
            CheckResourceViews();
            ListAccessibility.RequireItemNames(resourceList, "Resource list");
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
            if (smokeCompactShell is null) { smokeCompactShell = CheckCompactShellAsync(); return; }
            if (!smokeCompactShell.IsCompleted) return;
            smokeCompactShell.GetAwaiter().GetResult();
            if (smokeKeyboard is null) { CheckHighContrast(); smokeKeyboard = CheckKeyboardTraversalAsync(); return; }
            if (!smokeKeyboard.IsCompleted) return;
            smokeKeyboard.GetAwaiter().GetResult();
            if (smokeSnapshotLoss is null) { smokeSnapshotLoss = CheckProcessSnapshotLossAsync(); return; }
            if (!smokeSnapshotLoss.IsCompleted) return;
            smokeSnapshotLoss.GetAwaiter().GetResult();
            if (smokeSnapshotComparison is null) { smokeSnapshotComparison = SnapshotComparisonUiChecks.RunAsync(); return; }
            if (!smokeSnapshotComparison.IsCompleted) return;
            smokeSnapshotComparison.GetAwaiter().GetResult();
            App.Trace($"aot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported};ready={smokeModelsChecked};models={smokeModelsChecked};minimized={smokeMinimizedChecked};services={smokeServicesChecked};columns={smokeColumnsChecked};export=True;comparison=True;processes={model.Processes.Count};resources={model.Resources.Count};charts={coreCharts.Count}");
            RequestMainClose();
        }
    }
}
