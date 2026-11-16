using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace NksTaskMgr.WinUI;

/// <summary>Captured process samples at the selected chart time: one short header, the metric switch and the table;
/// retention, precision and identity details stay available in tooltips and the selected-row line.</summary>
internal sealed class HistoricalProcessesWindow : Window
{
    private readonly Grid root = new() { Padding = new Thickness(16, 12, 16, 12), RowSpacing = 8, Background = Display.Brush(0x171d21) };
    private readonly TextBlock when = Text("", 14, 0xe9eef0), system = Text("", 12, 0x95a4ad), count = Text("", 12, 0x95a4ad), gaps = Text("", 12, 0xe0b04a), selected = Text("", 12, 0x95a4ad);
    private readonly Border info = new() { Child = Text("ⓘ", 15, 0x95a4ad), Padding = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly ProcessHistoryRows rows = new();
    private readonly ProcessHistoryTemplates templates = new();
    private readonly Dictionary<ProcessHistoryMetric, Button> metricButtons = [];
    private ProcessHistorySelection selection;
    private ProcessHistoryMetric metric;
    private readonly TextBlock[] headingLabels = new TextBlock[6];
    private readonly ProcessTimeline? timeline;
    private readonly Expander lifecycle = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly ListView lifecycleList = new() { SelectionMode = ListViewSelectionMode.None, MaxHeight = 150 };

    internal HistoricalProcessesWindow(ProcessHistorySelection selection, string systemSummary, ProcessTimeline? timeline = null)
    {
        this.selection = selection; this.timeline = timeline;
        Title = Loc.T("Historické procesy · NKS Task Manager"); AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico")); AppWindow.ResizeDip(900, 640);
        Appearance.Attach(this, root);
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(when); header.Children.Add(system); header.Children.Add(gaps); header.Children.Add(info);
        AutomationProperties.SetName(info, Loc.T("Podrobnosti o uchovaných datech"));
        root.Children.Add(header);

        var bar = new Grid { ColumnSpacing = 12 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); bar.ColumnDefinitions.Add(new ColumnDefinition());
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var value in new[] { ProcessHistoryMetric.Cpu, ProcessHistoryMetric.Memory, ProcessHistoryMetric.Io }) {
            var button = Command(value == ProcessHistoryMetric.Cpu ? "CPU" : value == ProcessHistoryMetric.Memory ? "RAM" : "I/O");
            ToolTipService.SetToolTip(button, value == ProcessHistoryMetric.Cpu ? Loc.T("Řadit podle CPU") : value == ProcessHistoryMetric.Memory ? Loc.T("Řadit podle pracovní sady") : Loc.T("Řadit podle procesového I/O"));
            button.Click += (_, _) => { metric = value; ShowRows(); }; metricButtons.Add(value, button); modes.Children.Add(button);
        }
        bar.Children.Add(modes);
        count.HorizontalAlignment = HorizontalAlignment.Right; count.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(count, 1); bar.Children.Add(count);
        Grid.SetRow(bar, 1); root.Children.Add(bar);

        var table = new Grid { MinWidth = 760 }; table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); table.RowDefinitions.Add(new RowDefinition());
        var headings = new Grid { Padding = new Thickness(10, 6, 10, 6), ColumnSpacing = 12, Background = Display.Brush(0x1d252a) };
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), new GridLength(70), new GridLength(120), new GridLength(150), new GridLength(150), new GridLength(120) }) headings.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var hints = new[] { "", Loc.T("Původní PID"), "", Loc.T("Pracovní sada"), Loc.T("Čtení a zápis procesu, bez rozlišení disku, sítě a GPU"), "" };
        for (int i = 0; i < headingLabels.Length; ++i) {
            var label = headingLabels[i] = Text("", 12, 0x95a4ad); if (i > 0) label.HorizontalAlignment = HorizontalAlignment.Right;
            if (hints[i].Length > 0) ToolTipService.SetToolTip(label, hints[i]);
            Grid.SetColumn(label, i); headings.Children.Add(label);
        }
        table.Children.Add(headings); list.ItemsSource = rows; list.ItemTemplate = templates["HistoricalProcess"].As<DataTemplate>();
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30d));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); list.ItemContainerStyle = style;
        Grid.SetRow(list, 1); table.Children.Add(list);
        var scroll = new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        scroll.SizeChanged += (_, args) => table.Width = Math.Max(760, args.NewSize.Width);
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        selected.TextTrimming = TextTrimming.CharacterEllipsis; selected.TextWrapping = TextWrapping.NoWrap;
        Grid.SetRow(selected, 3); root.Children.Add(selected);
        AutomationProperties.SetName(list, Loc.T("Neměnné historické procesové vzorky")); AutomationProperties.SetAutomationId(list, "historical-processes-list");
        AutomationProperties.SetName(selected, Loc.T("Původní identita a čas vybraného historického procesu"));
        list.SelectionChanged += (_, _) => ShowSelected();
        lifecycle.Content = lifecycleList; Grid.SetRow(lifecycle, 4); root.Children.Add(lifecycle);
        AutomationProperties.SetAutomationId(lifecycleList, "historical-lifecycle-list");
        Content = root; ShowSelection(selection, systemSummary);
    }

    internal void ShowSelection(ProcessHistorySelection value, string systemSummary)
    {
        selection = value; system.Text = systemSummary; system.Visibility = systemSummary.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        when.Text = value.FrameCount == 0 ? Loc.T("Žádný zachycený snímek") : value.Point
            ? ProcessHistoryText.Time(value.FirstSelectedNs, value.FirstWallTimeUnixNs) + "  ·  " + Loc.F("{0} od kurzoru", (value.PointOffsetNs > 0 ? "+" : "") + Display.Number(value.PointOffsetNs / 1_000_000d, " ms", 0))
            : ProcessHistoryText.Time(value.FirstSelectedNs, value.FirstWallTimeUnixNs) + " – " + ProcessHistoryText.Time(value.LastSelectedNs, value.LastWallTimeUnixNs) + "  ·  " + string.Format(Loc.Culture, MainWindow.PluralForm(value.FrameCount, Loc.T("{0} snímek"), Loc.T("{0} snímky"), Loc.T("{0} snímků")), value.FrameCount);
        gaps.Text = value.GapCount == 0 ? "" : Loc.F("⚠ {0} {1} v záznamu", value.GapCount, MainWindow.PluralForm(value.GapCount, Loc.T("mezera"), Loc.T("mezery"), Loc.T("mezer")));
        gaps.Visibility = value.GapCount == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(gaps, value.Gaps.Count == 0 ? null : string.Join("\n", value.Gaps.Take(8).Select(gap => ProcessHistoryText.Mono(gap.BeforeNs) + " → " + ProcessHistoryText.Mono(gap.AfterNs)))
            + Loc.F("\nZnámé vynechané revize předání: {0}", value.KnownMissingRevisions));
        ToolTipService.SetToolTip(info, value.Notice
            + Loc.F("\nUloženo {0} z {1} řádků. Každý snímek ukládá jen 8 procesů s nejvyšším CPU, 8 s nejvyšší RAM a 8 s nejvyšším I/O.", value.StoredRows, value.OriginalRows)
            + (value.FrameCount == 0 ? "" : Loc.F("\nVe snímku bylo původně {0}–{1} procesů.", value.MinimumOriginalCount, value.MaximumOriginalCount))
            + Loc.F("\nUchovaný rozsah {0} – {1}: {2} snímků, {3} řádků, {4} ze 4 MiB.", ProcessHistoryText.Mono(value.FirstRetainedNs), ProcessHistoryText.Mono(value.LastRetainedNs),
                value.RetainedFrames, value.RetainedRows, Display.Number(value.AccountedBytes / 1048576d, "", 2))
            + Loc.F("\nVytěsněno {0} snímků ({1} řádků).", value.EvictedFrames, value.EvictedRows)
            + (value.DeliverySequenceUnknown ? Loc.T("\nÚplnost číslování předání nelze určit.") : "")
            + Loc.T("\nObecné I/O procesu se nepřiřazuje k disku, síti ani GPU; úplnost původního sběru nelze potvrdit."));
        ShowRows(); ShowLifecycle();
    }

    /// <summary>Starts and ends of processes inside the selected range, from the application's process timeline; the range edges are wall-clock times of the first and last selected snapshot.</summary>
    private void ShowLifecycle()
    {
        var value = selection;
        if (timeline is null || value.Point || value.FrameCount == 0 || value.FirstWallTimeUnixNs <= 0 || value.LastWallTimeUnixNs <= 0) { lifecycle.Visibility = Visibility.Collapsed; return; }
        var from = DateTimeOffset.FromUnixTimeMilliseconds(value.FirstWallTimeUnixNs / 1_000_000).LocalDateTime;
        var to = DateTimeOffset.FromUnixTimeMilliseconds(value.LastWallTimeUnixNs / 1_000_000).LocalDateTime;
        var events = ProcessEventRow.Build(timeline.Entries, ProcessEventFilter.All, "", new()).Where(row => row.When >= from && row.When <= to).ToArray();
        lifecycle.Header = Text(Loc.F("Spuštěné a ukončené v úseku: {0}", events.Length), 12, 0xe9eef0);
        lifecycleList.ItemsSource = events.Length == 0 ? [Loc.T("V úseku se nespustil ani neukončil žádný proces.")]
            : events.Select(row => row.TimeText + "  " + row.EventText + "  " + row.NameText + " (PID " + row.PidText + ")  ·  "
                + (row.Started ? Loc.T("rodič") + " " + row.ParentText : row.DurationText + (row.ExitText == "—" ? "" : "  ·  " + Loc.T("kód") + " " + row.ExitText))).ToArray();
        lifecycle.Visibility = Visibility.Visible;
    }

    private void ShowRows()
    {
        bool point = selection.Point;
        string[] names = point ? [Loc.T("Proces"), "PID", "CPU", "RAM", "I/O", Loc.T("Čas")] : [Loc.T("Proces"), "PID", Loc.T("CPU Ø / max"), Loc.T("RAM Ø / max"), Loc.T("I/O Ø / max"), Loc.T("Snímky")];
        for (int i = 0; i < names.Length; ++i) headingLabels[i].Text = names[i];
        ToolTipService.SetToolTip(headingLabels[5], point ? Loc.T("Skutečný čas vzorku") : Loc.T("V kolika snímcích úseku se proces objevil"));
        if (point) rows.Replace(selection.Rows(metric).Select(sample => new ProcessHistoryRow(sample)));
        else rows.Replace(selection.Summaries.OrderByDescending(summary => summary.Total(metric)).Select(summary => new ProcessHistoryRow(summary, selection.FrameCount)));
        count.Text = rows.Count == 0 ? "" : point ? Loc.F("{0} z {1} procesů", rows.Count, selection.MaximumOriginalCount) : string.Format(Loc.Culture, MainWindow.PluralForm(rows.Count, Loc.T("{0} proces"), Loc.T("{0} procesy"), Loc.T("{0} procesů")), rows.Count);
        ToolTipService.SetToolTip(list, point ? Loc.T("Všechny zachycené řádky nejbližšího snímku.") : Loc.T("Průměr a maximum z uložených vzorků. Nahoře je proces, který v úseku spotřeboval nejvíc."));
        foreach (var (kind, button) in metricButtons) button.Background = Display.Brush(kind == metric ? 0x28343bu : 0x1d252au);
        if (rows.Count > 0) list.SelectedIndex = 0; else ShowSelected();
    }

    private void ShowSelected()
    {
        if (list.SelectedItem is ProcessHistoryRow row) {
            var sample = row.Sample;
            string seen = row.Summary is { } summary && summary.Samples > 1
                ? ProcessHistoryText.Time(summary.First.TimeNs, summary.First.WallTimeUnixNs) + " – " + ProcessHistoryText.Time(sample.TimeNs, sample.WallTimeUnixNs)
                : ProcessHistoryText.Time(sample.TimeNs, sample.WallTimeUnixNs);
            selected.Text = sample.Name + "  ·  PID " + sample.Key.Pid + "  ·  " + seen + (sample.Late ? Loc.T("  ·  opožděný vzorek") : "");
            ToolTipService.SetToolTip(selected, ProcessHistoryText.Detail(sample));
        } else {
            selected.Text = rows.Count == 0 ? Loc.T("Pro tento výběr nejsou zachycené platné hodnoty zvolené metriky.") : "";
            ToolTipService.SetToolTip(selected, null);
        }
    }

    private static TextBlock Text(string value, double size, uint color) => new() { Text = value, FontFamily = UiTypography.Font, FontSize = size,
        Foreground = Display.Brush(color), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private static Button Command(string text) => new() { Content = text, FontFamily = UiTypography.Font, FontSize = 12, MinHeight = 0, Height = 28, MinWidth = 56, Padding = new Thickness(12, 0, 12, 0), Background = Display.Brush(0x1d252a) };
}
