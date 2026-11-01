using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using NksTaskMgr.Native;
using Windows.Graphics;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Button compareSnapshots = Button("");
    private SnapshotComparisonView? snapshotComparison;
    private Task? smokeSnapshotComparison;

    private void ConfigureSnapshotComparison()
    {
        compareSnapshots.Width = 35; compareSnapshots.Padding = new Thickness(0);
        compareSnapshots.Content = UiIcon.Create("compare", 0x95a4ad, 19);
        AutomationProperties.SetName(compareSnapshots, "Porovnat snímky");
        AutomationProperties.SetAutomationId(compareSnapshots, "snapshot-compare");
        ToolTipService.SetToolTip(compareSnapshots, "Porovnat snímky");
        if (App.SmokePath is not null) SnapshotComparisonUiChecks.CommandAvailable(compareSnapshots, model.ProcessTime);
        compareSnapshots.Click += (_, _) => {
            if (snapshotComparison is null) {
                snapshotComparison = new SnapshotComparisonView();
                snapshotComparison.Closed += (_, _) => snapshotComparison = null;
            }
            snapshotComparison.Activate();
        };
        Closed += (_, _) => snapshotComparison?.Close();
    }
}

internal sealed partial class SnapshotComparisonView : Window
{
    private static readonly CultureInfo Czech = CultureInfo.GetCultureInfo("cs-CZ");
    private static readonly ProcessComparisonKind[] Kinds = Enum.GetValues<ProcessComparisonKind>();
    private readonly Grid layout = new() { Padding = new Thickness(16), RowSpacing = 8, RequestedTheme = ElementTheme.Dark, Background = Display.Brush(0x171d21) };
    private readonly Button beforeButton = Command("Vybrat původní JSON…"), afterButton = Command("Vybrat následující JSON…");
    private readonly Button compare = Command("Porovnat"), cancel = Command("Zrušit načítání");
    private readonly TextBlock beforeFile = Text("Soubor není vybraný"), afterFile = Text("Soubor není vybraný");
    private readonly TextBlock status = Text("Vyberte původní a následující snímek."), summary = Text("");
    private readonly ComboBox filter = new() { Width = 200, MinHeight = 32 };
    private readonly TextBlock shown = Text("");
    private readonly ListView rows = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0) };
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12, Text = "Vyberte proces ze seznamu." };
    private SnapshotComparisonResult? result;
    private ProcessComparison[] visible = [];
    private CancellationTokenSource? operation;
    private string? beforePath, afterPath;
    private bool picking, closed;

    internal SnapshotComparisonView()
    {
        Title = "Porovnat snímky · NKS Task Manager";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.Resize(new SizeInt32(1100, 820));
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        var files = new Grid { RowSpacing = 6, ColumnSpacing = 12 };
        files.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); files.ColumnDefinitions.Add(new ColumnDefinition());
        files.RowDefinitions.Add(new RowDefinition()); files.RowDefinitions.Add(new RowDefinition());
        beforeButton.MinWidth = afterButton.MinWidth = 210;
        files.Children.Add(beforeButton); files.Children.Add(beforeFile); Grid.SetColumn(beforeFile, 1);
        files.Children.Add(afterButton); Grid.SetRow(afterButton, 1);
        files.Children.Add(afterFile); Grid.SetColumn(afterFile, 1); Grid.SetRow(afterFile, 1);
        foreach (var file in new[] { beforeFile, afterFile }) { file.VerticalAlignment = VerticalAlignment.Center; file.TextTrimming = TextTrimming.CharacterEllipsis; }
        layout.Children.Add(files);
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        compare.MinWidth = 110; compare.Background = Display.Brush(0xef835f); compare.Foreground = Display.Brush(0x171d21);
        actions.Children.Add(compare); actions.Children.Add(cancel); Grid.SetColumn(cancel, 1);
        status.TextWrapping = TextWrapping.Wrap; status.VerticalAlignment = VerticalAlignment.Center;
        actions.Children.Add(status); Grid.SetColumn(status, 2); Grid.SetRow(actions, 1); layout.Children.Add(actions);
        summary.TextWrapping = TextWrapping.Wrap; summary.IsTextSelectionEnabled = true;
        var metadata = new ScrollViewer { Content = summary, MaxHeight = 88, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(metadata, 2); layout.Children.Add(metadata);
        var results = new Grid { RowSpacing = 6 };
        foreach (var height in new[] { GridLength.Auto, new GridLength(3, GridUnitType.Star), GridLength.Auto, new GridLength(2, GridUnitType.Star) })
            results.RowDefinitions.Add(new RowDefinition { Height = height });
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        filter.Items.Add("Vše"); foreach (var kind in Kinds) filter.Items.Add(KindText(kind)); filter.SelectedIndex = 0;
        filters.Children.Add(filter); shown.VerticalAlignment = VerticalAlignment.Center; filters.Children.Add(shown); results.Children.Add(filters);
        rows.SelectionChanged += (_, _) => UpdateDetail();
        var rowStyle = new Style { TargetType = typeof(ListViewItem) };
        rowStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34d));
        rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
        rows.ItemContainerStyle = rowStyle;
        ScrollViewer.SetHorizontalScrollBarVisibility(rows, ScrollBarVisibility.Disabled);
        Grid.SetRow(rows, 1); results.Children.Add(rows);
        var detailTitle = Text("Detail vybraného procesu · pouze pro čtení"); Grid.SetRow(detailTitle, 2); results.Children.Add(detailTitle);
        ScrollViewer.SetVerticalScrollBarVisibility(detail, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(detail, ScrollBarVisibility.Auto);
        Grid.SetRow(detail, 3); results.Children.Add(detail);
        Grid.SetRow(results, 3); layout.Children.Add(results); Content = layout;
        Identify(layout, "snapshot-comparison", "Porovnání historických snímků");
        Identify(beforeButton, "comparison-before", "Vybrat původní snímek JSON"); Identify(afterButton, "comparison-after", "Vybrat následující snímek JSON");
        Identify(compare, "comparison-run", "Porovnat vybrané snímky"); Identify(cancel, "comparison-cancel", "Zrušit načítání a porovnání");
        Identify(status, "comparison-status", ""); Identify(summary, "comparison-summary", "");
        Identify(filter, "comparison-filter", "Filtrovat podle výsledku porovnání"); Identify(rows, "comparison-processes", "Výsledky porovnání procesů");
        Identify(detail, "comparison-detail", "Původní a následující hodnoty vybraného procesu");
        beforeButton.Click += async (_, _) => await ChooseFileAsync(true);
        afterButton.Click += async (_, _) => await ChooseFileAsync(false);
        compare.Click += async (_, _) => await CompareFilesAsync(); cancel.Click += (_, _) => CancelComparison();
        filter.SelectionChanged += (_, _) => FilterRows();
        Closed += (_, _) => { closed = true; operation?.Cancel(); };
        UpdateCommands();
    }

    private static Button Command(string label) => new() { Content = label, Height = 35, MinHeight = 0,
        FontFamily = UiTypography.Font, FontSize = 12, Padding = new Thickness(12, 0, 12, 0) };
    private static TextBlock Text(string value) => new() { Text = value, FontFamily = UiTypography.Font, FontSize = 12, Foreground = Display.Brush(0xd7dfe3) };
    private static void Identify(DependencyObject element, string id, string name) { AutomationProperties.SetAutomationId(element, id); if (name.Length > 0) AutomationProperties.SetName(element, name); }

    private async Task ChooseFileAsync(bool before)
    {
        if (operation is not null || picking) return;
        picking = true; UpdateCommands();
        try {
            var picker = new FileOpenPicker(AppWindow.Id) { Title = before ? "Původní snímek JSON" : "Následující snímek JSON" };
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (closed || file is null) return;
            if (before) beforePath = file.Path; else afterPath = file.Path;
            UpdateFileNames(); ClearResult(); status.Text = "Soubory se porovnají v pořadí původní → následující.";
        } catch (Exception error) when (error is COMException or ArgumentException or UnauthorizedAccessException) {
            if (!closed) status.Text = "Soubor se nepodařilo vybrat: " + error.Message;
        } finally { picking = false; if (!closed) UpdateCommands(); }
    }

    internal async Task CompareFilesAsync(string? before = null, string? after = null)
    {
        if (operation is not null || picking || closed) return;
        if (before is not null) beforePath = before;
        if (after is not null) afterPath = after;
        if (beforePath is null || afterPath is null) return;
        UpdateFileNames(); ClearResult();
        using var current = new CancellationTokenSource(); operation = current; UpdateCommands();
        string role = "Původní snímek";
        try {
            status.Text = "Načítám původní snímek…";
            var first = await SnapshotComparison.ReadAsync(beforePath, current.Token);
            role = "Následující snímek"; if (!closed) status.Text = "Načítám následující snímek…";
            var second = await SnapshotComparison.ReadAsync(afterPath, current.Token);
            role = "Porovnání"; if (!closed) status.Text = "Porovnávám procesy…";
            var compared = await SnapshotComparison.CompareAsync(first, second, current.Token);
            current.Token.ThrowIfCancellationRequested();
            if (!closed) ShowResult(compared);
        } catch (OperationCanceledException) { if (!closed) status.Text = "Načítání nebo porovnání bylo zrušeno."; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) {
            if (!closed) status.Text = role + ": " + error.Message;
        } finally { operation = null; if (!closed) UpdateCommands(); }
    }

    private void UpdateFileNames()
    {
        beforeFile.Text = beforePath is null ? "Soubor není vybraný" : Path.GetFileName(beforePath);
        afterFile.Text = afterPath is null ? "Soubor není vybraný" : Path.GetFileName(afterPath);
        ToolTipService.SetToolTip(beforeFile, beforePath); ToolTipService.SetToolTip(afterFile, afterPath);
    }

    private void UpdateCommands()
    {
        beforeButton.IsEnabled = afterButton.IsEnabled = operation is null && !picking;
        compare.IsEnabled = operation is null && !picking && beforePath is not null && afterPath is not null;
        cancel.IsEnabled = operation is not null; filter.IsEnabled = result is not null;
    }

    private void ClearResult()
    {
        result = null; visible = []; rows.ItemsSource = null; detail.Text = "Vyberte proces ze seznamu.";
        summary.Text = shown.Text = ""; filter.IsEnabled = false;
    }

    internal void ShowResult(SnapshotComparisonResult value)
    {
        result = value;
        string Time(ImportedSnapshot snapshot) => snapshot.Metadata.CapturedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", Czech);
        summary.Text = "Původní: " + Time(value.Before) + "\nNásledující: " + Time(value.After) + "\n"
            + string.Join(" · ", Kinds.Select(kind => KindText(kind) + ": " + value.Processes.Count(row => row.Kind == kind).ToString(Czech)));
        var reasons = value.Processes.Where(row => row.Kind == ProcessComparisonKind.NotComparable).Select(row => row.Reason).Distinct().ToArray();
        if (reasons.Length > 0) summary.Text += "\n" + string.Join(" ", reasons.Select(ReasonText));
        status.Text = "Porovnání dokončeno."; FilterRows(); UpdateCommands();
    }

    private void FilterRows()
    {
        var selected = rows.SelectedIndex >= 0 && rows.SelectedIndex < visible.Length ? visible[rows.SelectedIndex] : null;
        visible = result?.Processes.Where(row => filter.SelectedIndex <= 0 || row.Kind == Kinds[filter.SelectedIndex - 1]).ToArray() ?? [];
        rows.ItemsSource = visible.Select(RowText).ToArray();
        shown.Text = visible.Length.ToString(Czech) + " procesů";
        int position = selected is null ? -1 : Array.IndexOf(visible, selected);
        rows.SelectedIndex = position >= 0 ? position : visible.Length > 0 ? 0 : -1;
        UpdateDetail();
    }

    private static string RowText(ProcessComparison row)
    {
        var process = row.After ?? row.Before!;
        string? name = row.After?.Name ?? row.Before?.Name;
        if (name is { Length: > 120 }) name = name[..(char.IsHighSurrogate(name[119]) ? 119 : 120)] + "…";
        string side = row.Kind == ProcessComparisonKind.NotComparable ? row.Before is null ? " · následující" : row.After is null ? " · původní" : "" : "";
        return KindText(row.Kind) + " · PID " + process.Key.Pid.ToString(Czech) + (name is null ? "" : " · " + name) + side;
    }

    private void UpdateDetail()
    {
        if (rows.SelectedIndex < 0 || rows.SelectedIndex >= visible.Length) { detail.Text = "Vyberte proces ze seznamu."; return; }
        var row = visible[rows.SelectedIndex];
        var text = new StringBuilder(RowText(row));
        if (row.Reason != SnapshotComparisonReason.None) text.AppendLine().Append(ReasonText(row.Reason));
        void Identity(string label, ImportedProcess? process) {
            text.AppendLine().AppendLine().Append(label).Append(": ");
            if (process is null) { text.Append("Proces v tomto snímku není."); return; }
            text.Append(process.Name ?? "Název není součástí snímku.").AppendLine()
                .Append("PID ").Append(process.Key.Pid).Append(" · ").Append(process.Key.Kind).Append(" ").Append(process.Key.Generation)
                .Append(" · identita ").Append(process.IdentityVerified ? "ověřená" : "neověřená");
        }
        Identity("Původní", row.Before); Identity("Následující", row.After);
        text.AppendLine().AppendLine().Append("Rozdíl = následující - původní");
        foreach (var metric in row.Metrics.OrderBy(metric => MetricOrder(metric.Name))) {
            text.AppendLine().AppendLine().AppendLine(MetricName(metric.Name))
                .Append("  Původní: ").AppendLine(MetricText(metric.Before))
                .Append("  Následující: ").AppendLine(MetricText(metric.After))
                .Append("  Rozdíl: ").Append(DeltaText(metric));
        }
        detail.Text = text.ToString();
    }

    internal static string KindText(ProcessComparisonKind kind) => kind switch {
        ProcessComparisonKind.Created => "Nové", ProcessComparisonKind.Ended => "Ukončené", ProcessComparisonKind.Changed => "Změněné",
        ProcessComparisonKind.Unchanged => "Beze změny", _ => "Nelze porovnat"
    };
    internal static string ReasonText(SnapshotComparisonReason reason) => reason switch {
        SnapshotComparisonReason.BootNotVerified => "U snímku nelze ověřit spuštění systému.",
        SnapshotComparisonReason.BootChanged => "Snímky pocházejí z různých spuštění systému.",
        SnapshotComparisonReason.WeakIdentity => "Životnost procesu není spolehlivě ověřená.",
        SnapshotComparisonReason.GenerationKindChanged => "Druh identity procesu se mezi snímky liší.",
        SnapshotComparisonReason.IncompleteProcessStream => "Snímek procesů není úplný nebo aktuální.", _ => ""
    };
    private static int MetricOrder(string name) => name switch { "cpu" => 0, "workingSet" => 1, "privateMemory" => 2, "readIo" => 3, "writeIo" => 4, "threadCount" => 5, "handleCount" => 6, _ => 7 };
    private static string MetricName(string name) => name switch { "cpu" => "Procesor", "workingSet" => "Pracovní sada", "privateMemory" => "Soukromá paměť", "readIo" => "Čtení I/O", "writeIo" => "Zápis I/O", "threadCount" => "Vlákna", "handleCount" => "Popisovače", _ => name };
    private static string Unit(string unit) => unit switch { "percentOfMachine" => "% počítače", "percent" => "%", "bytes" => "B", "bytesPerSecond" => "B/s", "bitsPerSecond" => "bit/s", "count" => "", _ => unit };
    private static string State(SampleState state) => state switch {
        SampleState.Valid => "Platné", SampleState.First => "První vzorek", SampleState.Unavailable => "Nedostupné", SampleState.AccessDenied => "Chybí oprávnění",
        SampleState.Unsupported => "Nepodporováno", SampleState.Stale => "Zastaralé", SampleState.Error => "Chyba", _ => "Mezera v měření"
    };
    internal static string MetricText(ImportedMetric? metric)
    {
        if (metric is null) return "Pole není ve snímku.";
        string? value = metric.State != SampleState.Valid ? null : metric.UnsignedValue?.ToString("N0", Czech) ?? metric.Value?.ToString("G6", Czech);
        return (value is null ? State(metric.State) : value + " " + Unit(metric.Unit)) + " [" + State(metric.State) + "; " + metric.Source + "; " + Unit(metric.Unit) + "]";
    }
    internal static string DeltaText(ComparedMetric metric)
    {
        string unit = metric.After?.Unit ?? metric.Before?.Unit ?? "";
        string suffix = unit is "percent" or "percentOfMachine" ? "p. b." : Unit(unit);
        if (metric.IntegerDelta is decimal integer) return (integer > 0 ? "+" : "") + integer.ToString("N0", Czech) + " " + suffix;
        if (metric.FloatingDelta is double floating) return (floating > 0 ? "+" : "") + floating.ToString("G6", Czech) + " " + suffix;
        return "Nelze určit.";
    }

    internal void CancelComparison() => operation?.Cancel();
    internal bool IsBusy => operation is not null;
    internal ListView ResultsList => rows;
    internal ComboBox StatusFilter => filter;
    internal string SummaryText => summary.Text;
    internal string StatusText => status.Text;
    internal string DetailText => detail.Text;
    internal Grid Root => layout;
    internal IReadOnlyList<Button> Commands => [beforeButton, afterButton, compare, cancel];
}
