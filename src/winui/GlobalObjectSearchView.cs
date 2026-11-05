using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;
using Windows.Graphics;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Button globalObjectSearchButton = Button("");
    private GlobalObjectSearchView? globalObjectSearch;

    private void ConfigureGlobalObjectSearch()
    {
        globalObjectSearchButton.Width = 35; globalObjectSearchButton.Padding = new Thickness(0);
        globalObjectSearchButton.Content = UiIcon.Create("object-search", 0x95a4ad, 19);
        AutomationProperties.SetName(globalObjectSearchButton, "Hledat otevřené objekty ve všech dostupných procesech");
        AutomationProperties.SetAutomationId(globalObjectSearchButton, "global-object-search");
        ToolTipService.SetToolTip(globalObjectSearchButton, "Hledat otevřené objekty podle názvu nebo typu");
        globalObjectSearchButton.Click += (_, _) => {
            if (globalObjectSearch is null) {
                globalObjectSearch = new GlobalObjectSearchView(processDetailsClient,
                    () => model.BootIdentity is { State: SampleState.Valid } boot && !boot.Identifier.IsEmpty ? boot.Identifier : null,
                    key => CurrentObjectOwner(key) is not null, OpenObjectOwner);
                globalObjectSearch.Closed += (_, _) => globalObjectSearch = null;
            }
            globalObjectSearch.Activate();
        };
        Closed += (_, _) => globalObjectSearch?.Close();
    }

    private ProcessRow? CurrentObjectOwner(ProcessKey key)
    {
        if (model.Paused || model.Stale || model.ProcessError.Length != 0 || model.BootIdentity.State != SampleState.Valid
            || model.BootIdentity.Identifier != key.Boot || NativeMonitor.MonotonicNowNs - model.ProcessTime > MonitorModel.StaleAfterNs) return null;
        return model.Processes.FirstOrDefault(row => row.Key == key && model.IsCurrent(row));
    }

    private bool OpenObjectOwner(ProcessKey key)
    {
        if (CurrentObjectOwner(key) is not { } row) return false;
        processQuery = ""; model.SetQuery(""); model.SetMode(ProcessMode.All); ShowView(false); SelectProcess(row); Activate();
        return true;
    }
}

internal sealed class GlobalObjectSearchView : Window
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue ui;
    private readonly GlobalObjectSearchCoordinator coordinator;
    private readonly Func<BootIdentifier?> getBoot;
    private readonly Func<ProcessKey, bool> isCurrent;
    private readonly Func<ProcessKey, bool> openOwner;
    private readonly Grid layout = new() { Padding = new Thickness(12), RowSpacing = 6, Background = Display.Brush(0x171d21) };
    private readonly Grid body = new() { ColumnSpacing = 12, RowSpacing = 8 };
    private readonly TextBox name = new() { PlaceholderText = @"Část názvu nebo cesty, například C:\složka\soubor", MaxLength = 1024, MinHeight = 32 };
    private readonly TextBox type = new() { PlaceholderText = "Například File, Event nebo Mutant", MaxLength = 128, MinHeight = 32 };
    private readonly Button start = Command("Spustit"), cancel = Command("Zrušit"), open = Command("Vybrat živý proces →");
    private readonly TextBlock status = Label("Zadejte alespoň dva znaky do názvu nebo typu. Hledání se spouští tlačítkem."), progress = Label(""), count = Label("0 výsledků");
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0) };
    private readonly ObservableCollection<GlobalObjectSearchResult> results = [];
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12, MinHeight = 0, Text = "Vyberte výsledek ze seznamu." };
    private readonly DispatcherTimer availability = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private GlobalObjectSearchProgress? lastProgress;
    private bool dirty, liveLinks;
    private volatile bool closed;

    internal GlobalObjectSearchView(ProcessDetailsClient client, Func<BootIdentifier?> getBoot,
        Func<ProcessKey, bool> isCurrent, Func<ProcessKey, bool> openOwner)
    {
        ui = DispatcherQueue; coordinator = new(client); this.getBoot = getBoot; this.isCurrent = isCurrent; this.openOwner = openOwner;
        Title = "Hledat otevřené objekty · NKS Task Manager";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico")); AppWindow.ResizeDip(1100, 780);
        Appearance.Attach(this, layout);
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        var query = new Grid { ColumnSpacing = 10 };
        query.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); query.ColumnDefinitions.Add(new ColumnDefinition());
        var names = new StackPanel { Spacing = 4 }; names.Children.Add(Label("Název objektu nebo cesta obsahuje")); names.Children.Add(name); query.Children.Add(names);
        var types = new StackPanel { Spacing = 4 }; types.Children.Add(Label("Typ objektu obsahuje")); types.Children.Add(type); query.Children.Add(types); Grid.SetColumn(types, 1); layout.Children.Add(query);
        var commands = new Grid { ColumnSpacing = 8 };
        commands.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); commands.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); commands.ColumnDefinitions.Add(new ColumnDefinition());
        commands.Children.Add(start); commands.Children.Add(cancel); Grid.SetColumn(cancel, 1);
        status.TextWrapping = TextWrapping.Wrap; status.MaxHeight = 52; status.VerticalAlignment = VerticalAlignment.Center;
        commands.Children.Add(status); Grid.SetColumn(status, 2); Grid.SetRow(commands, 1); layout.Children.Add(commands);
        progress.TextWrapping = TextWrapping.Wrap; progress.IsTextSelectionEnabled = true;
        var summary = new ScrollViewer { Content = progress, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(summary, 2); layout.Children.Add(summary);
        body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition());
        body.RowDefinitions.Add(new RowDefinition()); body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        var template = new GlobalObjectSearchTemplates(); list.ItemTemplate = (DataTemplate)template["ObjectResult"];
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        list.ItemContainerStyle = style; list.ItemsSource = results;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled); body.Children.Add(list);
        ScrollViewer.SetVerticalScrollBarVisibility(detail, ScrollBarVisibility.Auto); ScrollViewer.SetHorizontalScrollBarVisibility(detail, ScrollBarVisibility.Auto);
        body.Children.Add(detail); Grid.SetColumn(detail, 1); Grid.SetRow(body, 3); layout.Children.Add(body);
        var footer = new Grid { ColumnSpacing = 10 }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        count.VerticalAlignment = VerticalAlignment.Center; count.TextWrapping = TextWrapping.Wrap; footer.Children.Add(count); footer.Children.Add(open); Grid.SetColumn(open, 1); Grid.SetRow(footer, 4); layout.Children.Add(footer);
        Identify(name, "object-search-name", "Část názvu objektu nebo cesty"); Identify(type, "object-search-type", "Část typu objektu");
        Identify(start, "object-search-start", "Spustit nové hledání napříč dostupnými procesy"); Identify(cancel, "object-search-cancel", "Zrušit hledání");
        Identify(list, "object-search-results", "Pozorované otevřené objekty a jejich vlastníci"); Identify(detail, "object-search-detail", "Údaje vybraného objektu pouze pro čtení");
        Identify(open, "object-search-process", "Vybrat proces pouze při shodě celé aktuální identity"); Identify(progress, "object-search-progress", "Průběh a omezení hledání");
        name.TextChanged += (_, _) => QueryChanged(); type.TextChanged += (_, _) => QueryChanged();
        start.Click += async (_, _) => await SearchAsync(); cancel.Click += (_, _) => {
            liveLinks = false; open.IsEnabled = false; operation?.Cancel(); cancel.IsEnabled = false; status.Text = "Ruším hledání…";
        };
        list.SelectionChanged += (_, _) => { UpdateDetail(); UpdateAvailability(); };
        open.Click += (_, _) => {
            if (!liveLinks || dirty || list.SelectedItem is not GlobalObjectSearchResult row || !isCurrent(row.Owner) || !openOwner(row.Owner)) {
                open.IsEnabled = false; status.Text = "Historické pozorování: shodný aktuální proces není dostupný. Žádná akce nebyla provedena.";
            }
        };
        layout.SizeChanged += (_, _) => Resize(); availability.Tick += (_, _) => UpdateAvailability();
        Closed += (_, _) => { closed = true; availability.Stop(); lifetime.Cancel(); if (operation is { } active) active.Cancel(); else lifetime.Dispose(); };
        Content = layout; availability.Start(); UpdateAvailability(); Resize();
    }

    private void QueryChanged()
    {
        dirty = lastProgress is not null;
        if (dirty) status.Text = "Pole obsahují nový dotaz. Výsledky patří předchozímu hledání; použijte Spustit.";
        UpdateAvailability();
    }

    private async Task SearchAsync()
    {
        if (operation is not null || !new GlobalObjectSearchQuery(name.Text, type.Text).IsValid) return;
        if (getBoot() is not { } boot) { status.Text = "Identitu spuštění Windows zatím nelze ověřit. Počkejte na sběr dat a zkuste to znovu."; return; }
        using var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); operation = current;
        results.Clear(); lastProgress = null; dirty = false; liveLinks = true; detail.Text = "Vyberte výsledek ze seznamu.";
        count.Text = "0 výsledků · pouze čtení";
        status.Text = "Načítám vlastníky otevřených objektů…"; progress.Text = "Limity: 2 minuty, 25 000 dotazů, 4 096 objektů na vlastníka, 5 000 výsledků a 8 MiB textu. Nedostupné položky se přeskočí.";
        UpdateAvailability();
        try { await coordinator.SearchAsync(boot, new(name.Text, type.Text), DeliverAsync, current.Token); }
        catch (OperationCanceledException) when (current.IsCancellationRequested || closed) {
            if (!closed) { liveLinks = false; status.Text = "Hledání bylo zrušené; výsledky jsou neúplná historická pozorování."; }
        } catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or ObjectDisposedException or ArgumentException) {
            if (!closed) { liveLinks = false; status.Text = "Hledání nelze dokončit: " + error.Message; }
        } finally {
            operation = null;
            if (closed) lifetime.Dispose(); else UpdateAvailability();
        }
    }

    private async Task DeliverAsync(GlobalObjectSearchBatch batch, CancellationToken cancellation)
    {
        if (closed) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = linked.Token;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = token.Register(() => done.TrySetCanceled(token));
        if (!ui.TryEnqueue(() => {
            try {
                if (!closed && !token.IsCancellationRequested) ApplyBatch(batch);
                done.TrySetResult(true);
            } catch (Exception error) { done.TrySetException(error); }
        })) { lifetime.Cancel(); return; }
        await done.Task.ConfigureAwait(false);
    }

    private void ApplyBatch(GlobalObjectSearchBatch batch)
    {
        foreach (var row in batch.Results) results.Add(row);
        var value = lastProgress = batch.Progress;
        string total = value.TotalOwners < 0 ? "?" : value.TotalOwners.ToString(CultureInfo.InvariantCulture);
        string remaining = value.TotalOwners < 0 ? "?" : Math.Max(0, value.TotalOwners - value.ExaminedOwners).ToString(CultureInfo.InvariantCulture);
        progress.Text = $"Vlastníci: {value.ExaminedOwners}/{total} · prohledaní {value.SearchedOwners} · s nedostupnými údaji {value.UnavailableOwners} · změnění/ukončení {value.EndedOwners} · omezení {value.LimitedOwners} · zbývá {remaining}\n"
            + $"Dotazy na objekty: {value.ObjectsExamined} · přeskočené položky: {value.ObjectsSkipped} · shody: {value.Matches}"
            + (value.Partial ? "\nČástečné výsledky: další objekty či vlastníci mohou chybět." : "") + (value.Note.Length > 0 ? "\n" + value.Note : "");
        status.Text = !value.Finished ? "Prohledávám " + value.CurrentOwner
            : value.Cancelled ? "Zrušeno · výsledky jsou neúplná historická pozorování."
            : value.Partial ? "Průchod není úplný; chybějící shoda neznamená, že objekt není otevřený."
            : "Průchod dokončen. Výsledky jsou pozorování v době dotazu.";
        if (value.Cancelled) liveLinks = false;
        count.Text = results.Count + " výsledků · pouze čtení";
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        if (closed) return;
        bool busy = operation is not null;
        start.IsEnabled = !busy && new GlobalObjectSearchQuery(name.Text, type.Text).IsValid;
        start.Content = lastProgress is null || dirty ? "Spustit" : "Zopakovat";
        cancel.IsEnabled = busy && lastProgress?.Finished != true && !operation!.IsCancellationRequested;
        name.IsEnabled = type.IsEnabled = !busy;
        open.IsEnabled = liveLinks && !dirty && list.SelectedItem is GlobalObjectSearchResult row && isCurrent(row.Owner);
        ToolTipService.SetToolTip(open, open.IsEnabled ? "Vybrat shodný aktuální proces v hlavním okně." : "Historická nebo neověřitelná identita nemá živou akci.");
    }

    private void UpdateDetail()
    {
        if (list.SelectedItem is not GlobalObjectSearchResult row) { detail.Text = "Vyberte výsledek ze seznamu."; return; }
        var item = row.Observation;
        var text = new StringBuilder().AppendLine(row.OwnerText).AppendLine("Handle: " + row.HandleText)
            .AppendLine("Typ: " + item.Type + " · " + GlobalObjectSearchText.State(item.TypeState) + " · NTSTATUS 0x" + item.TypeNtStatus.ToString("X8"))
            .AppendLine("Pozorovaný název: " + (item.NameState == SampleState.Valid && item.Name.Length == 0 ? "(bez názvu)" : item.Name))
            .AppendLine("Stav názvu: " + GlobalObjectSearchText.State(item.NameState) + " · NTSTATUS 0x" + item.NameNtStatus.ToString("X8"));
        if (item.NameState == SampleState.Valid && row.DisplayName != item.Name) text.AppendLine("DOS/UNC zobrazení: " + row.DisplayName);
        text.AppendLine("Přístup ze seznamu: 0x" + row.Handle.GrantedAccess.ToString("X8") + " · atributy 0x" + row.Handle.Attributes.ToString("X"))
            .AppendLine("Přijato: " + row.ReceivedAt.ToLocalTime().ToString("G", CultureInfo.GetCultureInfo("cs-CZ")))
            .AppendLine("Boot: " + row.Owner.Boot.Low.ToString("X16") + row.Owner.Boot.High.ToString("X16"))
            .AppendLine("Generace: " + row.Owner.Kind + " · " + row.Owner.Generation)
            .AppendLine().Append("Název a typ patří pozorovanému duplikátu. Číselná hodnota handle mohla být od výpisu znovu použita. Objekty nelze zavírat ani měnit.");
        detail.Text = text.ToString();
    }

    private void Resize()
    {
        bool stacked = layout.ActualWidth < 850;
        body.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 1, GridUnitType.Star);
        body.RowDefinitions[1].Height = new GridLength(stacked ? 1 : 0, GridUnitType.Star);
        Grid.SetColumn(detail, stacked ? 0 : 1); Grid.SetRow(detail, stacked ? 1 : 0);
    }

    private static Button Command(string value) => new() { Content = value, MinHeight = 32, Padding = new Thickness(10, 0, 10, 0), FontSize = 12, FontFamily = UiTypography.Font };
    private static TextBlock Label(string value) => new() { Text = value, FontSize = 12, FontFamily = UiTypography.Font, Foreground = Display.Brush(0x95a4ad) };
    private static void Identify(DependencyObject element, string id, string name) { AutomationProperties.SetAutomationId(element, id); AutomationProperties.SetName(element, name); }
}
