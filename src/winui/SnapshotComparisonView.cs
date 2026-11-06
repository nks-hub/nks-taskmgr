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
        AutomationProperties.SetName(compareSnapshots, Loc.T("Porovnat snímky"));
        AutomationProperties.SetAutomationId(compareSnapshots, "snapshot-compare");
        ToolTipService.SetToolTip(compareSnapshots, Loc.T("Porovnat snímky"));
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
    private static readonly ProcessComparisonKind[] Kinds = Enum.GetValues<ProcessComparisonKind>();
    private readonly Grid layout = new() { Padding = new Thickness(16), RowSpacing = 8, Background = Display.Brush(0x171d21) };
    private readonly Button beforeButton = Command(Loc.T("Vybrat původní JSON…")), afterButton = Command(Loc.T("Vybrat následující JSON…"));
    private readonly Button compare = Command(Loc.T("Porovnat")), cancel = Command(Loc.T("Zrušit načítání"));
    private readonly TextBlock beforeFile = Text(Loc.T("Soubor není vybraný")), afterFile = Text(Loc.T("Soubor není vybraný"));
    private readonly TextBlock status = Text(Loc.T("Vyberte původní a následující snímek.")), summary = Text("");
    private readonly ComboBox filter = new() { Width = 200, MinHeight = 32 };
    private readonly TextBlock shown = Text("");
    private readonly ListView rows = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0) };
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12, Text = Loc.T("Vyberte proces ze seznamu.") };
    private SnapshotComparisonResult? result;
    private ProcessComparison[] visible = [];
    private CancellationTokenSource? operation;
    private string? beforePath, afterPath;
    private bool picking, closed;

    internal SnapshotComparisonView()
    {
        Title = Loc.T("Porovnat snímky") + " · NKS Task Manager";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        Appearance.Attach(this, layout);
        AppWindow.ResizeDip(1100, 820);
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
        filter.Items.Add(Loc.T("Vše")); foreach (var kind in Kinds) filter.Items.Add(KindText(kind)); filter.SelectedIndex = 0;
        filters.Children.Add(filter); shown.VerticalAlignment = VerticalAlignment.Center; filters.Children.Add(shown); results.Children.Add(filters);
        rows.SelectionChanged += (_, _) => UpdateDetail();
        var rowStyle = new Style { TargetType = typeof(ListViewItem) };
        rowStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34d));
        rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
        rows.ItemContainerStyle = rowStyle;
        ScrollViewer.SetHorizontalScrollBarVisibility(rows, ScrollBarVisibility.Disabled);
        Grid.SetRow(rows, 1); results.Children.Add(rows);
        var detailTitle = Text(Loc.T("Detail vybraného procesu · pouze pro čtení")); Grid.SetRow(detailTitle, 2); results.Children.Add(detailTitle);
        ScrollViewer.SetVerticalScrollBarVisibility(detail, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(detail, ScrollBarVisibility.Auto);
        Grid.SetRow(detail, 3); results.Children.Add(detail);
        Grid.SetRow(results, 3); layout.Children.Add(results); Content = layout;
        Identify(layout, "snapshot-comparison", Loc.T("Porovnání historických snímků"));
        Identify(beforeButton, "comparison-before", Loc.T("Vybrat původní snímek JSON")); Identify(afterButton, "comparison-after", Loc.T("Vybrat následující snímek JSON"));
        Identify(compare, "comparison-run", Loc.T("Porovnat vybrané snímky")); Identify(cancel, "comparison-cancel", Loc.T("Zrušit načítání a porovnání"));
        Identify(status, "comparison-status", ""); Identify(summary, "comparison-summary", "");
        Identify(filter, "comparison-filter", Loc.T("Filtrovat podle výsledku porovnání")); Identify(rows, "comparison-processes", Loc.T("Výsledky porovnání procesů"));
        Identify(detail, "comparison-detail", Loc.T("Původní a následující hodnoty vybraného procesu"));
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
            var picker = new FileOpenPicker(AppWindow.Id) { Title = before ? Loc.T("Původní snímek JSON") : Loc.T("Následující snímek JSON") };
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (closed || file is null) return;
            if (before) beforePath = file.Path; else afterPath = file.Path;
            UpdateFileNames(); ClearResult(); status.Text = Loc.T("Soubory se porovnají v pořadí původní → následující.");
        } catch (Exception error) when (error is COMException or ArgumentException or UnauthorizedAccessException) {
            if (!closed) status.Text = Loc.F("Soubor se nepodařilo vybrat: {0}", error.Message);
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
        string role = Loc.T("Původní snímek");
        try {
            status.Text = Loc.T("Načítám původní snímek…");
            var first = await SnapshotComparison.ReadAsync(beforePath, current.Token);
            role = Loc.T("Následující snímek"); if (!closed) status.Text = Loc.T("Načítám následující snímek…");
            var second = await SnapshotComparison.ReadAsync(afterPath, current.Token);
            role = Loc.T("Porovnání"); if (!closed) status.Text = Loc.T("Porovnávám procesy…");
            var compared = await SnapshotComparison.CompareAsync(first, second, current.Token);
            current.Token.ThrowIfCancellationRequested();
            if (!closed) ShowResult(compared);
        } catch (OperationCanceledException) { if (!closed) status.Text = Loc.T("Načítání nebo porovnání bylo zrušeno."); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) {
            if (!closed) status.Text = role + ": " + error.Message;
        } finally { operation = null; if (!closed) UpdateCommands(); }
    }

    private void UpdateFileNames()
    {
        beforeFile.Text = beforePath is null ? Loc.T("Soubor není vybraný") : Path.GetFileName(beforePath);
        afterFile.Text = afterPath is null ? Loc.T("Soubor není vybraný") : Path.GetFileName(afterPath);
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
        result = null; visible = []; rows.ItemsSource = null; detail.Text = Loc.T("Vyberte proces ze seznamu.");
        summary.Text = shown.Text = ""; filter.IsEnabled = false;
    }

    internal void ShowResult(SnapshotComparisonResult value)
    {
        result = value;
        string Time(ImportedSnapshot snapshot) => snapshot.Metadata.CapturedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", Loc.Culture);
        summary.Text = Loc.T("Původní") + ": " + Time(value.Before) + "\n" + Loc.T("Následující") + ": " + Time(value.After) + "\n"
            + string.Join(" · ", Kinds.Select(kind => KindText(kind) + ": " + value.Processes.Count(row => row.Kind == kind).ToString(Loc.Culture)));
        var reasons = value.Processes.Where(row => row.Kind == ProcessComparisonKind.NotComparable).Select(row => row.Reason).Distinct().ToArray();
        if (reasons.Length > 0) summary.Text += "\n" + string.Join(" ", reasons.Select(ReasonText));
        status.Text = Loc.T("Porovnání dokončeno."); FilterRows(); UpdateCommands();
    }

    private void FilterRows()
    {
        var selected = rows.SelectedIndex >= 0 && rows.SelectedIndex < visible.Length ? visible[rows.SelectedIndex] : null;
        visible = result?.Processes.Where(row => filter.SelectedIndex <= 0 || row.Kind == Kinds[filter.SelectedIndex - 1]).ToArray() ?? [];
        rows.ItemsSource = visible.Select(RowText).ToArray();
        shown.Text = Loc.F("{0} procesů", visible.Length);
        int position = selected is null ? -1 : Array.IndexOf(visible, selected);
        rows.SelectedIndex = position >= 0 ? position : visible.Length > 0 ? 0 : -1;
        UpdateDetail();
    }

    private static string RowText(ProcessComparison row)
    {
        var process = row.After ?? row.Before!;
        string? name = row.After?.Name ?? row.Before?.Name;
        if (name is { Length: > 120 }) name = name[..(char.IsHighSurrogate(name[119]) ? 119 : 120)] + "…";
        string side = row.Kind == ProcessComparisonKind.NotComparable ? row.Before is null ? " · " + Loc.T("následující") : row.After is null ? " · " + Loc.T("původní") : "" : "";
        return KindText(row.Kind) + " · PID " + process.Key.Pid.ToString(Loc.Culture) + (name is null ? "" : " · " + name) + side;
    }

    private void UpdateDetail()
    {
        if (rows.SelectedIndex < 0 || rows.SelectedIndex >= visible.Length) { detail.Text = Loc.T("Vyberte proces ze seznamu."); return; }
        var row = visible[rows.SelectedIndex];
        var text = new StringBuilder(RowText(row));
        if (row.Reason != SnapshotComparisonReason.None) text.AppendLine().Append(ReasonText(row.Reason));
        void Identity(string label, ImportedProcess? process) {
            text.AppendLine().AppendLine().Append(label).Append(": ");
            if (process is null) { text.Append(Loc.T("Proces v tomto snímku není.")); return; }
            text.Append(process.Name ?? Loc.T("Název není součástí snímku.")).AppendLine()
                .Append("PID ").Append(process.Key.Pid).Append(" · ").Append(process.Key.Kind).Append(" ").Append(process.Key.Generation)
                .Append(" · ").Append(process.IdentityVerified ? Loc.T("identita ověřená") : Loc.T("identita neověřená"));
        }
        Identity(Loc.T("Původní"), row.Before); Identity(Loc.T("Následující"), row.After);
        text.AppendLine().AppendLine().Append(Loc.T("Rozdíl = následující - původní"));
        foreach (var metric in row.Metrics.OrderBy(metric => MetricOrder(metric.Name))) {
            text.AppendLine().AppendLine().AppendLine(MetricName(metric.Name))
                .Append("  " + Loc.T("Původní") + ": ").AppendLine(MetricText(metric.Before))
                .Append("  " + Loc.T("Následující") + ": ").AppendLine(MetricText(metric.After))
                .Append("  " + Loc.T("Rozdíl") + ": ").Append(DeltaText(metric));
        }
        detail.Text = text.ToString();
    }

    internal static string KindText(ProcessComparisonKind kind) => kind switch {
        ProcessComparisonKind.Created => Loc.T("Nové"), ProcessComparisonKind.Ended => Loc.T("Ukončené"), ProcessComparisonKind.Changed => Loc.T("Změněné"),
        ProcessComparisonKind.Unchanged => Loc.T("Beze změny"), _ => Loc.T("Nelze porovnat")
    };
    internal static string ReasonText(SnapshotComparisonReason reason) => reason switch {
        SnapshotComparisonReason.BootNotVerified => Loc.T("U snímku nelze ověřit spuštění systému."),
        SnapshotComparisonReason.BootChanged => Loc.T("Snímky pocházejí z různých spuštění systému."),
        SnapshotComparisonReason.WeakIdentity => Loc.T("Životnost procesu není spolehlivě ověřená."),
        SnapshotComparisonReason.GenerationKindChanged => Loc.T("Druh identity procesu se mezi snímky liší."),
        SnapshotComparisonReason.IncompleteProcessStream => Loc.T("Snímek procesů není úplný nebo aktuální."), _ => ""
    };
    private static int MetricOrder(string name) => name switch { "cpu" => 0, "workingSet" => 1, "privateMemory" => 2, "readIo" => 3, "writeIo" => 4, "threadCount" => 5, "handleCount" => 6, _ => 7 };
    private static string MetricName(string name) => name switch { "cpu" => Loc.T("Procesor"), "workingSet" => Loc.T("Pracovní sada"), "privateMemory" => Loc.T("Soukromá paměť"), "readIo" => Loc.T("Čtení I/O"), "writeIo" => Loc.T("Zápis I/O"), "threadCount" => Loc.T("Vlákna"), "handleCount" => Loc.T("Popisovače"), _ => name };
    private static string Unit(string unit) => unit switch { "percentOfMachine" => Loc.T("% počítače"), "percent" => "%", "bytes" => "B", "bytesPerSecond" => "B/s", "bitsPerSecond" => "bit/s", "count" => "", _ => unit };
    private static string State(SampleState state) => state switch {
        SampleState.Valid => Loc.T("Platné"), SampleState.First => Loc.T("První vzorek"), SampleState.Unavailable => Loc.T("Nedostupné"), SampleState.AccessDenied => Loc.T("Chybí oprávnění"),
        SampleState.Unsupported => Loc.T("Nepodporováno"), SampleState.Stale => Loc.T("Zastaralé"), SampleState.Error => Loc.T("Chyba"), _ => Loc.T("Mezera v měření")
    };
    internal static string MetricText(ImportedMetric? metric)
    {
        if (metric is null) return Loc.T("Pole není ve snímku.");
        string? value = metric.State != SampleState.Valid ? null : metric.UnsignedValue?.ToString("N0", Loc.Culture) ?? metric.Value?.ToString("G6", Loc.Culture);
        return (value is null ? State(metric.State) : value + " " + Unit(metric.Unit)) + " [" + State(metric.State) + "; " + metric.Source + "; " + Unit(metric.Unit) + "]";
    }
    internal static string DeltaText(ComparedMetric metric)
    {
        string unit = metric.After?.Unit ?? metric.Before?.Unit ?? "";
        string suffix = unit is "percent" or "percentOfMachine" ? Loc.T("p. b.") : Unit(unit);
        if (metric.IntegerDelta is decimal integer) return (integer > 0 ? "+" : "") + integer.ToString("N0", Loc.Culture) + " " + suffix;
        if (metric.FloatingDelta is double floating) return (floating > 0 ? "+" : "") + floating.ToString("G6", Loc.Culture) + " " + suffix;
        return Loc.T("Nelze určit.");
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
