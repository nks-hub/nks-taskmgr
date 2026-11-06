using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed class StartupRow(StartupEntry entry)
{
    internal StartupEntry Entry { get; } = entry;
    internal TextBlock? PublisherLabel { get; set; }
    /// <summary>Run, Run32 and Startup folder items switch through StartupApproved like Windows; RunOnce items cannot be switched.</summary>
    internal bool Switchable => Entry.Source is StartupSource.UserRun or StartupSource.UserFolder or StartupSource.MachineRun
        or StartupSource.MachineRun32 or StartupSource.CommonFolder;
    /// <summary>Machine-wide items need an administrator; a standard user is offered a restart through UAC.</summary>
    internal bool NeedsAdministrator => Entry.Source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonFolder;
    internal string Title => Entry.Name.Length == 0 ? Loc.T("(výchozí hodnota)")
        : Entry.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Entry.Name[..^4] : Entry.Name;
    internal string StateText => Entry.State switch {
        StartupState.Enabled => Loc.T("Povoleno"), StartupState.Disabled => Loc.T("Zakázáno"), _ => Loc.T("Jednorázově")
    };
    internal string StateDetail => Entry.State switch {
        StartupState.Disabled when Entry.ChangedFileTime is > 0 and < 0x7fffffffffffffff
            => Loc.F("Zakázáno {0}", DateTime.FromFileTimeUtc((long)Entry.ChangedFileTime).ToLocalTime().ToString("g", Loc.Culture)),
        StartupState.RunOnce => Loc.T("Spustí se jednou při příštím přihlášení a Windows položku pak odstraní."),
        _ => StateText
    };
    internal string Scope => Entry.Source is StartupSource.UserRun or StartupSource.UserRunOnce or StartupSource.UserFolder ? Loc.T("Uživatel") : Loc.T("Celý počítač");
}

public sealed partial class MainWindow
{
    private readonly Button startupNav = Button(Loc.T("Spuštění")), startupReload = Button(Loc.T("Obnovit")), startupInfo = Button("ⓘ"),
        startupToggle = Button(Loc.T("Vypnout")), startupOpen = Button(Loc.T("Otevřít umístění"));
    private readonly Grid startupPage = new();
    private readonly ListView startupList = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly TextBlock startupCount = Label("", 11, 0x95a4ad), startupStatus = Label("", 11, 0x95a4ad), startupEmpty = Label(Loc.T("Načítám položky po spuštění…"), 12, 0x95a4ad);
    private readonly Image startupDetailIcon = new() { Width = 32, Height = 32 };
    private readonly TextBlock startupDetailTitle = Label("", 16, 0xe9eef0), startupDetailState = Label("", 12, 0x95a4ad);
    private readonly StackPanel startupFacts = new() { Spacing = 10 };
    private readonly Grid startupDetail = new() { RowSpacing = 12, Padding = new Thickness(16), Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, string> startupPublishers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<ImageSource?>> startupIcons = new(StringComparer.OrdinalIgnoreCase);
    private List<StartupRow> startupRows = [];
    private int startupGeneration;
    private bool startupLoaded;
    private static readonly GridLength[] StartupColumns = [new(1, GridUnitType.Star), new(200), new(96)];

    private void BuildStartup()
    {
        ToolTipService.SetToolTip(startupNav, Loc.T("Aplikace spouštěné po přihlášení"));
        startupNav.Click += (_, _) => ShowView(DesktopView.Startup);
        startupPage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) }); startupPage.RowDefinitions.Add(new RowDefinition());
        var toolbar = new Grid { ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        startupCount.VerticalAlignment = VerticalAlignment.Center; left.Children.Add(startupCount);
        startupStatus.VerticalAlignment = VerticalAlignment.Center; startupStatus.TextTrimming = TextTrimming.CharacterEllipsis; left.Children.Add(startupStatus);
        toolbar.Children.Add(left);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        startupInfo.Width = 32; startupInfo.Padding = new Thickness(0);
        ToolTipService.SetToolTip(startupInfo, Loc.T("Položky vlastního účtu se zapínají a vypínají stejně jako ve Windows (StartupApproved), nic se nemaže.\nPoložky pro celý počítač a jednorázové položky NKS jen zobrazuje; změnit je může správce."));
        AutomationProperties.SetName(startupInfo, Loc.T("Jak funguje zapnutí a vypnutí"));
        foreach (var button in new[] { startupInfo, startupReload }) { button.Height = 28; commands.Children.Add(button); }
        startupReload.Click += (_, _) => { startupPublishers.Clear(); _ = LoadStartupAsync(); };
        AutomationProperties.SetName(startupReload, Loc.T("Znovu načíst položky po spuštění"));
        Grid.SetColumn(commands, 1); toolbar.Children.Add(commands); startupPage.Children.Add(toolbar);

        var table = new Grid(); table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) }); table.RowDefinitions.Add(new RowDefinition());
        var header = StartupGrid(); header.BorderBrush = Display.Brush(0x2c363d); header.BorderThickness = new Thickness(0, 0, 0, 1);
        string[] headings = [Loc.T("Název"), Loc.T("Vydavatel"), Loc.T("Stav")];
        for (int i = 0; i < headings.Length; ++i) StartupCell(header, i, headings[i], 0x95a4ad).Margin = new Thickness(i == 0 ? 26 : 0, 0, 0, 0);
        table.Children.Add(header);
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34d));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); startupList.ItemContainerStyle = style;
        startupList.ContextRequested += StartupContextRequested;
        startupList.SelectionChanged += (_, _) => RefreshStartupDetail();
        AutomationProperties.SetName(startupList, Loc.T("Aplikace po spuštění"));
        AutomationProperties.SetAutomationId(startupList, "startup-list");
        Grid.SetRow(startupList, 1); table.Children.Add(startupList);
        startupEmpty.HorizontalAlignment = HorizontalAlignment.Center; startupEmpty.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(startupEmpty, 1); table.Children.Add(startupEmpty);

        // Detail of the selected item: everything the table no longer spells out, plus its commands.
        startupDetail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); startupDetail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        startupDetail.RowDefinitions.Add(new RowDefinition());
        var title = new Grid { ColumnSpacing = 10 };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); title.ColumnDefinitions.Add(new ColumnDefinition());
        var names = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        startupDetailTitle.TextWrapping = TextWrapping.Wrap; names.Children.Add(startupDetailTitle); names.Children.Add(startupDetailState);
        title.Children.Add(startupDetailIcon); title.Children.Add(names); Grid.SetColumn(names, 1);
        startupDetail.Children.Add(title);
        var detailCommands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var button in new[] { startupToggle, startupOpen }) { button.Height = 30; detailCommands.Children.Add(button); }
        startupToggle.Click += async (_, _) => { if (SelectedStartup() is { } row) await SetStartupEnabledAsync(row, row.Entry.State != StartupState.Enabled); };
        startupOpen.Click += (_, _) => { if (SelectedStartup() is { Entry.Target.Length: > 0 } row) OpenFileLocation(row.Entry.Target); };
        AutomationProperties.SetAutomationId(startupToggle, "startup-toggle"); AutomationProperties.SetAutomationId(startupOpen, "startup-open");
        Grid.SetRow(detailCommands, 1); startupDetail.Children.Add(detailCommands);
        var facts = new ScrollViewer { Content = startupFacts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(facts, 2); startupDetail.Children.Add(facts);
        AutomationProperties.SetAutomationId(startupDetail, "startup-detail");

        var body = new Grid { ColumnSpacing = 12 };
        body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        body.Children.Add(Card(table));
        var detailCard = Card(startupDetail); Grid.SetColumn(detailCard, 1); body.Children.Add(detailCard);
        Grid.SetRow(body, 1); startupPage.Children.Add(body);
        startupPage.Visibility = Visibility.Collapsed; page.Children.Add(startupPage);
    }

    private StartupRow? SelectedStartup() => (startupList.SelectedItem as ListViewItem)?.Tag as StartupRow;

    private void ShowStartupPage(bool visible)
    {
        startupPage.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MarkNavigation(startupNav, visible);
        if (visible && !startupLoaded) { startupLoaded = true; _ = LoadStartupAsync(); }
    }

    private static Grid StartupGrid()
    {
        var grid = new Grid { Padding = new Thickness(12, 0, 12, 0), ColumnSpacing = 12 };
        foreach (var width in StartupColumns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        grid.ColumnDefinitions[0].MinWidth = 140;
        return grid;
    }

    private static TextBlock StartupCell(Grid grid, int column, string text, uint color, string? tip = null)
    {
        var cell = Label(text, 12, color); cell.VerticalAlignment = VerticalAlignment.Center; cell.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTipService.SetToolTip(cell, tip ?? (text.Length > 0 ? text : null));
        Grid.SetColumn(cell, column); grid.Children.Add(cell);
        return cell;
    }

    private async Task LoadStartupAsync()
    {
        int generation = ++startupGeneration;
        string? selected = SelectedStartup() is { } current ? current.Entry.Source + "|" + current.Entry.Name : null;
        startupEmpty.Text = Loc.T("Načítám položky po spuštění…"); startupEmpty.Visibility = Visibility.Visible;
        StartupEntry[] entries;
        try { entries = await Task.Run(NativeStartup.Read); }
        catch (Exception error) when (error is NativeMonitorException or DllNotFoundException or EntryPointNotFoundException) {
            if (generation != startupGeneration) return;
            startupRows = []; startupList.Items.Clear(); startupCount.Text = "";
            startupEmpty.Text = Loc.F("Seznam nelze načíst: {0}", error.Message); RefreshStartupDetail(); return;
        }
        if (generation != startupGeneration) return;
        startupRows = entries.Select(entry => new StartupRow(entry)).OrderBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        RenderStartup(selected);
        await VerifyStartupPublishersAsync(generation);
    }

    private void RenderStartup(string? selected)
    {
        startupList.Items.Clear();
        ListViewItem? reselect = null;
        foreach (var row in startupRows) {
            var grid = StartupGrid(); grid.Height = 34;
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            var icon = new Image { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            var title = Label(row.Title, 12, 0xe9eef0); title.VerticalAlignment = VerticalAlignment.Center; title.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Children.Add(icon); name.Children.Add(title); grid.Children.Add(name);
            _ = SetStartupIconAsync(row, icon);
            row.PublisherLabel = StartupCell(grid, 1, StartupPublisher(row), 0x95a4ad);
            StartupCell(grid, 2, row.StateText, row.Entry.State == StartupState.Enabled ? 0x83bca0u : 0x95a4adu, row.StateDetail);
            ToolTipService.SetToolTip(grid, row.Entry.Command);
            var item = new ListViewItem { Content = grid, Tag = row };
            AutomationProperties.SetName(item, row.Title + ", " + row.StateText + ", " + row.Scope);
            startupList.Items.Add(item);
            if (selected == row.Entry.Source + "|" + row.Entry.Name) reselect = item;
        }
        int enabled = startupRows.Count(row => row.Entry.State == StartupState.Enabled);
        startupCount.Text = Plural(startupRows.Count, "položka", "položky", "položek") + Loc.F(" · povoleno {0}", enabled);
        startupEmpty.Text = Loc.T("Žádné položky po spuštění.");
        startupEmpty.Visibility = startupRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        startupList.SelectedItem = reselect ?? (startupList.Items.Count > 0 ? startupList.Items[0] : null);
        RefreshStartupDetail();
    }

    private void RefreshStartupDetail()
    {
        if (SelectedStartup() is not { } row) { startupDetail.Visibility = Visibility.Collapsed; return; }
        startupDetail.Visibility = Visibility.Visible;
        startupDetailTitle.Text = row.Title; startupDetailState.Text = row.StateDetail + " · " + row.Scope;
        startupDetailIcon.Source = null; _ = SetStartupIconAsync(row, startupDetailIcon);
        startupToggle.Content = row.Entry.State == StartupState.Enabled ? Loc.T("Vypnout") : Loc.T("Zapnout");
        startupToggle.IsEnabled = row.Switchable && row.Entry.State != StartupState.RunOnce;
        if (row.NeedsAdministrator && !RunningElevated) startupToggle.Content += Loc.T(" (správce)");
        ToolTipService.SetToolTip(startupToggle, !row.Switchable ? Loc.T("Jednorázovou položku nelze vypnout.")
            : Loc.T("Změní jen značku Windows (StartupApproved); položka se nemaže.") + (row.NeedsAdministrator && !RunningElevated ? Loc.T(" Položka pro celý počítač vyžaduje správce, nabídne se výzva UAC.") : ""));
        startupOpen.IsEnabled = row.Entry.Target.Length > 0;
        startupFacts.Children.Clear();
        void Fact(string label, string value) {
            if (value.Length == 0) return;
            var block = new StackPanel { Spacing = 2 };
            block.Children.Add(Label(label, 11, 0x95a4ad));
            var text = Label(value, 12, 0xe9eef0); text.TextWrapping = TextWrapping.Wrap; text.IsTextSelectionEnabled = true;
            block.Children.Add(text); startupFacts.Children.Add(block);
        }
        Fact(Loc.T("Vydavatel"), StartupPublisher(row));
        Fact(Loc.T("Soubor"), row.Entry.Target);
        Fact(Loc.T("Příkaz"), row.Entry.Command);
        Fact(Loc.T("Umístění"), row.Entry.Location);
    }

    /// <summary>Shows the target file's shell icon; items without a file keep an empty slot so names stay aligned.</summary>
    private async Task SetStartupIconAsync(StartupRow row, Image image)
    {
        string path = row.Entry.Target;
        if (path.Length == 0 || !File.Exists(path)) return;
        if (!startupIcons.TryGetValue(path, out var task)) startupIcons[path] = task = LoadFileIconAsync(path);
        if (await task is { } source) image.Source = source;
    }

    private static async Task<ImageSource?> LoadFileIconAsync(string path)
    {
        try {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 32);
            if (thumbnail is null) return null;
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(thumbnail); return bitmap;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException) {
            return null;
        }
    }

    private static void OpenFileLocation(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + path + "\"") { UseShellExecute = false })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private string StartupPublisher(StartupRow row) => row.Entry.Target.Length == 0 ? Loc.T("Soubor nezjištěn")
        : startupPublishers.TryGetValue(row.Entry.Target, out var publisher) ? publisher : Loc.T("Ověřuji podpis…");

    private async Task VerifyStartupPublishersAsync(int generation)
    {
        for (int wait = 0; wait < 50 && model.BootIdentity is not { State: SampleState.Valid, Identifier.IsEmpty: false }; ++wait) await Task.Delay(100);
        foreach (string path in startupRows.Select(row => row.Entry.Target).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()) {
            if (generation != startupGeneration) return;
            if (!startupPublishers.ContainsKey(path)) {
                string text;
                if (model.BootIdentity is not { State: SampleState.Valid } boot || boot.Identifier.IsEmpty) text = Loc.T("Nezjištěno");
                else {
                    try { text = StartupSignatureText(await processDetailsClient.ReadFileSignatureAsync(boot.Identifier, path, CancellationToken.None)); }
                    catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException or ArgumentException or OperationCanceledException) { text = Loc.T("Nezjištěno"); }
                }
                startupPublishers[path] = text;
            }
            if (generation != startupGeneration) return;
            foreach (var row in startupRows.Where(row => string.Equals(row.Entry.Target, path, StringComparison.OrdinalIgnoreCase) && row.PublisherLabel is not null)) {
                row.PublisherLabel!.Text = startupPublishers[path]; ToolTipService.SetToolTip(row.PublisherLabel, startupPublishers[path] + "\n" + path);
            }
            if (SelectedStartup() is { } shown && string.Equals(shown.Entry.Target, path, StringComparison.OrdinalIgnoreCase)) RefreshStartupDetail();
        }
    }

    private static string StartupSignatureText(ProcessDetailsSnapshot result) => result.State switch {
        ProcessDetailsState.Ok when result.Signature is { } signature => signature.State switch {
            ModuleSignatureState.Valid => signature.Publisher.Length > 0 ? signature.Publisher : Loc.T("Podepsáno"),
            ModuleSignatureState.Unsigned => Loc.T("Bez podpisu"),
            ModuleSignatureState.Untrusted => Loc.T("Neplatný podpis") + (signature.Publisher.Length > 0 ? " · " + signature.Publisher : ""),
            _ => Loc.T("Nezjištěno")
        },
        _ when result.Error is 2 or 3 => Loc.T("Soubor nenalezen"),
        ProcessDetailsState.Unsupported => Loc.T("Mimo místní disk"),
        ProcessDetailsState.AccessDenied => Loc.T("Přístup odepřen"),
        _ => Loc.T("Nezjištěno")
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
            menu.Items.Add(ContextItem(enable ? Loc.T("Zapnout") : Loc.T("Vypnout"), enable ? "context-startup-enable" : "context-startup-disable", available,
                async () => await SetStartupEnabledAsync(row, enable)));
        }
        if (!row.Switchable) {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Jednorázovou položku nelze vypnout"), IsEnabled = false });
        }
        ShowContextMenu(menu, startupList, args);
    }

    private async Task SetStartupEnabledAsync(StartupRow row, bool enabled)
    {
        if (view != DesktopView.Startup || !startupRows.Contains(row) || !row.Switchable) return;
        if (row.NeedsAdministrator && !RunningElevated) {
            await OfferRestartElevatedAsync(Loc.F("Položku „{0}“ spouští Windows pro všechny uživatele, takže ji může změnit jen správce.", row.Title));
            return;
        }
        uint error;
        try { error = await Task.Run(() => NativeStartup.SetEnabled(row.Entry.Source, row.Entry.Name, enabled)); }
        catch (NativeMonitorException failure) { startupStatus.Text = Loc.F("Změnu nelze provést: {0}", failure.Message); await LoadStartupAsync(); return; }
        startupStatus.Text = error == 0 ? (enabled ? Loc.F("{0} se po přihlášení spustí.", row.Title) : Loc.F("{0} se po přihlášení nespustí.", row.Title))
            : Loc.F("Změnu nelze provést, Windows vrátil chybu {0}.", error.ToString(CultureInfo.InvariantCulture));
        await LoadStartupAsync();
    }
}
