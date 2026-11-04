using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed class StartupRow(StartupEntry entry)
{
    internal StartupEntry Entry { get; } = entry;
    internal TextBlock? PublisherLabel { get; set; }
    /// <summary>Only per-user items are switched without elevation, through the user's StartupApproved key.</summary>
    internal bool Switchable => Entry.Source is StartupSource.UserRun or StartupSource.UserFolder;
    internal string Title => Entry.Name.Length == 0 ? "(výchozí hodnota)"
        : Entry.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Entry.Name[..^4] : Entry.Name;
    internal string StateText => Entry.State switch {
        StartupState.Enabled => "Povoleno", StartupState.Disabled => "Zakázáno", _ => "Jednorázově"
    };
    internal string StateDetail => Entry.State switch {
        StartupState.Disabled when Entry.ChangedFileTime is > 0 and < 0x7fffffffffffffff
            => "Zakázáno " + DateTime.FromFileTimeUtc((long)Entry.ChangedFileTime).ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("cs-CZ")),
        StartupState.RunOnce => "Spustí se jednou při příštím přihlášení a Windows položku pak odstraní.",
        _ => StateText
    };
    internal string Scope => Entry.Source is StartupSource.UserRun or StartupSource.UserRunOnce or StartupSource.UserFolder ? "Uživatel" : "Celý počítač";
}

public sealed partial class MainWindow
{
    private readonly Button startupNav = Button("Po spuštění"), startupReload = Button("Obnovit");
    private readonly Grid startupPage = new();
    private readonly ListView startupList = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly TextBlock startupCount = Label("", 10, 0x95a4ad), startupStatus = Label("", 10, 0x95a4ad), startupEmpty = Label("Načítám položky po spuštění…", 12, 0x95a4ad);
    private readonly Dictionary<string, string> startupPublishers = new(StringComparer.OrdinalIgnoreCase);
    private List<StartupRow> startupRows = [];
    private int startupGeneration;
    private bool startupLoaded;
    private static readonly GridLength[] StartupColumns = [new(3, GridUnitType.Star), new(170), new(95), new(2, GridUnitType.Star), new(4, GridUnitType.Star)];

    private void BuildStartup()
    {
        if (startupNav.Content is StackPanel panel && panel.Children.Count == 2 && panel.Children[1] is TextBlock caption) {
            caption.TextWrapping = TextWrapping.Wrap; caption.TextAlignment = TextAlignment.Center; caption.MaxLines = 2;
            startupNav.Height = 58;
        }
        ToolTipService.SetToolTip(startupNav, "Aplikace spouštěné po přihlášení");
        startupNav.Click += (_, _) => ShowView(DesktopView.Startup);
        startupPage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) }); startupPage.RowDefinitions.Add(new RowDefinition());
        var toolbar = new Grid { Padding = new Thickness(16, 0, 16, 0), BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(0, 0, 0, 1) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        startupStatus.Text = "Položky pro celý počítač vyžadují práva správce, NKS je jen zobrazuje. Nic se nemaže, vypnutí jen označí položku jako Windows.";
        ToolTipService.SetToolTip(startupStatus, startupStatus.Text);
        startupStatus.VerticalAlignment = VerticalAlignment.Center; toolbar.Children.Add(startupStatus);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
        startupCount.VerticalAlignment = VerticalAlignment.Center; commands.Children.Add(startupCount);
        startupReload.Height = 27; startupReload.FontSize = 11; commands.Children.Add(startupReload);
        startupReload.Click += (_, _) => { startupPublishers.Clear(); _ = LoadStartupAsync(); };
        AutomationProperties.SetName(startupReload, "Znovu načíst položky po spuštění");
        Grid.SetColumn(commands, 1); toolbar.Children.Add(commands); startupPage.Children.Add(toolbar);
        var table = new Grid(); table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) }); table.RowDefinitions.Add(new RowDefinition());
        var header = StartupGrid(); header.Background = Display.Brush(0x1d252a);
        string[] headings = ["Název", "Vydavatel", "Stav", "Umístění", "Příkaz"];
        for (int i = 0; i < headings.Length; ++i) StartupCell(header, i, headings[i], 0x95a4ad);
        table.Children.Add(header);
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 32d));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); startupList.ItemContainerStyle = style;
        startupList.ContextRequested += StartupContextRequested;
        AutomationProperties.SetName(startupList, "Aplikace po spuštění");
        AutomationProperties.SetAutomationId(startupList, "startup-list");
        Grid.SetRow(startupList, 1); table.Children.Add(startupList);
        startupEmpty.HorizontalAlignment = HorizontalAlignment.Center; startupEmpty.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(startupEmpty, 1); table.Children.Add(startupEmpty);
        var body = Card(table); Grid.SetRow(body, 1); startupPage.Children.Add(body);
        startupPage.Visibility = Visibility.Collapsed; page.Children.Add(startupPage);
    }

    private void ShowStartupPage(bool visible)
    {
        startupPage.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        startupNav.Background = Display.Brush(visible ? 0x28343bu : 0x12181cu);
        if (visible && !startupLoaded) { startupLoaded = true; _ = LoadStartupAsync(); }
    }

    private static Grid StartupGrid()
    {
        var grid = new Grid { Padding = new Thickness(18, 0, 18, 0), ColumnSpacing = 12 };
        foreach (var width in StartupColumns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        grid.ColumnDefinitions[0].MinWidth = 140;
        return grid;
    }

    private static TextBlock StartupCell(Grid grid, int column, string text, uint color, string? tip = null)
    {
        var cell = Label(text, 12, color); cell.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(cell, tip ?? (text.Length > 0 ? text : null));
        Grid.SetColumn(cell, column); grid.Children.Add(cell);
        return cell;
    }

    private async Task LoadStartupAsync()
    {
        int generation = ++startupGeneration;
        startupEmpty.Text = "Načítám položky po spuštění…"; startupEmpty.Visibility = Visibility.Visible;
        StartupEntry[] entries;
        try { entries = await Task.Run(NativeStartup.Read); }
        catch (Exception error) when (error is NativeMonitorException or DllNotFoundException or EntryPointNotFoundException) {
            if (generation != startupGeneration) return;
            startupRows = []; startupList.Items.Clear(); startupCount.Text = "";
            startupEmpty.Text = "Seznam nelze načíst: " + error.Message; return;
        }
        if (generation != startupGeneration) return;
        startupRows = entries.Select(entry => new StartupRow(entry)).OrderBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        RenderStartup();
        await VerifyStartupPublishersAsync(generation);
    }

    private void RenderStartup()
    {
        startupList.Items.Clear();
        foreach (var row in startupRows) {
            var grid = StartupGrid(); grid.Height = 32;
            StartupCell(grid, 0, row.Title, 0xe9eef0, row.Entry.Name);
            row.PublisherLabel = StartupCell(grid, 1, StartupPublisher(row), 0x95a4ad);
            StartupCell(grid, 2, row.StateText, row.Entry.State == StartupState.Enabled ? 0x83bca0u : 0x95a4adu, row.StateDetail);
            StartupCell(grid, 3, row.Entry.Location, 0x95a4ad, row.Scope + " · " + row.Entry.Location + (row.Switchable ? "" : "\nZměna vyžaduje práva správce, NKS položku jen zobrazuje."));
            StartupCell(grid, 4, row.Entry.Command, 0x95a4ad, row.Entry.Command + (row.Entry.Target.Length > 0 ? "\nSoubor: " + row.Entry.Target : ""));
            var item = new ListViewItem { Content = grid, Tag = row };
            AutomationProperties.SetName(item, row.Title + ", " + row.StateText + ", " + row.Scope);
            startupList.Items.Add(item);
        }
        int enabled = startupRows.Count(row => row.Entry.State == StartupState.Enabled);
        startupCount.Text = startupRows.Count + " položek · povoleno " + enabled;
        startupEmpty.Text = "Žádné položky po spuštění.";
        startupEmpty.Visibility = startupRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string StartupPublisher(StartupRow row) => row.Entry.Target.Length == 0 ? "Soubor nezjištěn"
        : startupPublishers.TryGetValue(row.Entry.Target, out var publisher) ? publisher : "Ověřuji podpis…";

    private async Task VerifyStartupPublishersAsync(int generation)
    {
        for (int wait = 0; wait < 50 && model.BootIdentity is not { State: SampleState.Valid, Identifier.IsEmpty: false }; ++wait) await Task.Delay(100);
        foreach (string path in startupRows.Select(row => row.Entry.Target).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()) {
            if (generation != startupGeneration) return;
            if (!startupPublishers.ContainsKey(path)) {
                string text;
                if (model.BootIdentity is not { State: SampleState.Valid } boot || boot.Identifier.IsEmpty) text = "Nezjištěno";
                else {
                    try { text = StartupSignatureText(await processDetailsClient.ReadFileSignatureAsync(boot.Identifier, path, CancellationToken.None)); }
                    catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException or ArgumentException or OperationCanceledException) { text = "Nezjištěno"; }
                }
                startupPublishers[path] = text;
            }
            if (generation != startupGeneration) return;
            foreach (var row in startupRows.Where(row => string.Equals(row.Entry.Target, path, StringComparison.OrdinalIgnoreCase) && row.PublisherLabel is not null)) {
                row.PublisherLabel!.Text = startupPublishers[path]; ToolTipService.SetToolTip(row.PublisherLabel, startupPublishers[path] + "\n" + path);
            }
        }
    }

    private static string StartupSignatureText(ProcessDetailsSnapshot result) => result.State switch {
        ProcessDetailsState.Ok when result.Signature is { } signature => signature.State switch {
            ModuleSignatureState.Valid => signature.Publisher.Length > 0 ? signature.Publisher : "Podepsáno",
            ModuleSignatureState.Unsigned => "Bez podpisu",
            ModuleSignatureState.Untrusted => "Neplatný podpis" + (signature.Publisher.Length > 0 ? " · " + signature.Publisher : ""),
            _ => "Nezjištěno"
        },
        _ when result.Error is 2 or 3 => "Soubor nenalezen",
        ProcessDetailsState.Unsupported => "Mimo místní disk",
        ProcessDetailsState.AccessDenied => "Přístup odepřen",
        _ => "Nezjištěno"
    };

    private void StartupContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.Handled || view != DesktopView.Startup) return;
        args.Handled = true;
        activeContextMenu?.Hide();
        if (ContextRow(startupList, args) is not ListViewItem { Tag: StartupRow row }) return;
        var menu = new MenuFlyout();
        foreach (bool enable in new[] { true, false }) {
            bool available = row.Switchable && (row.Entry.State == StartupState.Enabled) != enable;
            menu.Items.Add(ContextItem(enable ? "Zapnout" : "Vypnout", enable ? "context-startup-enable" : "context-startup-disable", available,
                async () => await SetStartupEnabledAsync(row, enable)));
        }
        if (!row.Switchable) {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Text = row.Entry.State == StartupState.RunOnce ? "Jednorázovou položku nelze vypnout"
                : "Položku pro celý počítač mění jen správce", IsEnabled = false });
        }
        ShowContextMenu(menu, startupList, args);
    }

    private async Task SetStartupEnabledAsync(StartupRow row, bool enabled)
    {
        if (view != DesktopView.Startup || !startupRows.Contains(row) || !row.Switchable) return;
        uint error;
        try { error = await Task.Run(() => NativeStartup.SetEnabled(row.Entry.Source, row.Entry.Name, enabled)); }
        catch (NativeMonitorException failure) { startupStatus.Text = "Změnu nelze provést: " + failure.Message; await LoadStartupAsync(); return; }
        startupStatus.Text = error == 0 ? row.Title + (enabled ? " se po přihlášení spustí." : " se po přihlášení nespustí.")
            : "Změnu nelze provést, Windows vrátil chybu " + error.ToString(CultureInfo.InvariantCulture) + ".";
        await LoadStartupAsync();
    }
}
