using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT;

namespace NksTaskMgr.WinUI;

internal sealed class HistoricalProcessesWindow : Window
{
    private readonly Grid root = new() { Padding = new Thickness(18), RowSpacing = 10, Background = Display.Brush(0x171d21) };
    private readonly TextBlock summary = Text(""), precision = Text("");
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = UiTypography.Font, FontSize = 12, Height = 185 };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly ProcessHistoryRows rows = new();
    private readonly ProcessHistoryTemplates templates = new();
    private readonly Dictionary<ProcessHistoryMetric, Button> metricButtons = [];
    private ProcessHistorySelection selection;
    private ProcessHistoryMetric metric;

    internal HistoricalProcessesWindow(ProcessHistorySelection selection)
    {
        this.selection = selection;
        Title = "Historické procesy · NKS Task Manager"; AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico")); AppWindow.ResizeDip(1120, 840);
        Appearance.Attach(this, root);
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        root.Children.Add(Text("Zachycené procesové vzorky u vybraného času. Obecné I/O procesu nepřiřazuje provoz ke konkrétnímu disku, síti ani GPU. Příčinu nezachycené špičky z těchto dat nelze určit."));
        Grid.SetRow(summary, 1); root.Children.Add(summary);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var value in new[] { ProcessHistoryMetric.Cpu, ProcessHistoryMetric.Memory, ProcessHistoryMetric.Io }) {
            var button = Command(value == ProcessHistoryMetric.Cpu ? "CPU" : value == ProcessHistoryMetric.Memory ? "Pracovní sada" : "Procesové I/O");
            button.Click += (_, _) => { metric = value; ShowRows(); }; metricButtons.Add(value, button); modes.Children.Add(button);
        }
        Grid.SetRow(modes, 2); root.Children.Add(modes);
        var table = new Grid { MinWidth = 830 }; table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); table.RowDefinitions.Add(new RowDefinition());
        var headings = new Grid { Padding = new Thickness(10, 8, 10, 8), ColumnSpacing = 12, Background = Display.Brush(0x1d252a) };
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), new GridLength(70), new GridLength(85), new GridLength(110), new GridLength(120), new GridLength(135) }) headings.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var names = new[] { "Proces", "Původní PID", "CPU", "Pracovní sada", "I/O procesu", "Čas vzorku" };
        for (int i = 0; i < names.Length; ++i) { var label = Text(names[i]); if (i > 0) label.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(label, i); headings.Children.Add(label); }
        table.Children.Add(headings); list.ItemsSource = rows; list.ItemTemplate = templates["HistoricalProcess"].As<DataTemplate>();
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 32d));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); list.ItemContainerStyle = style;
        Grid.SetRow(list, 1); table.Children.Add(list);
        var scroll = new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        scroll.SizeChanged += (_, args) => table.Width = Math.Max(830, args.NewSize.Width);
        Grid.SetRow(scroll, 3); root.Children.Add(scroll); Grid.SetRow(detail, 4); root.Children.Add(detail); Grid.SetRow(precision, 5); root.Children.Add(precision);
        AutomationProperties.SetName(list, "Neměnné historické procesové vzorky"); AutomationProperties.SetAutomationId(list, "historical-processes-list");
        AutomationProperties.SetName(detail, "Původní identita, čas a stavy historického procesu pouze pro čtení");
        list.SelectionChanged += (_, _) => detail.Text = list.SelectedItem is ProcessHistoryRow row ? ProcessHistoryText.Detail(row.Sample) : "";
        root.SizeChanged += (_, _) => detail.Height = Math.Clamp(root.ActualHeight * 0.25, 90, 185);
        Content = root; ShowSelection(selection);
    }

    internal void ShowSelection(ProcessHistorySelection value)
    {
        selection = value;
        string when = value.FrameCount == 0 ? "Žádný odpovídající snímek" : value.Point
            ? "Skutečný vzorek " + ProcessHistoryText.Time(value.FirstSelectedNs, value.FirstWallTimeUnixNs)
                + " · odstup od kurzoru " + (value.PointOffsetNs > 0 ? "+" : "") + Display.Number(value.PointOffsetNs / 1_000_000d, " ms", 2)
            : "Skutečné vzorky " + ProcessHistoryText.Time(value.FirstSelectedNs, value.FirstWallTimeUnixNs) + " až " + ProcessHistoryText.Time(value.LastSelectedNs, value.LastWallTimeUnixNs);
        summary.Text = when + "\n" + value.FrameCount + " snímků · uchováno " + value.StoredRows + " z " + value.OriginalRows + " pozorování řádků · vynecháno " + (value.OriginalRows - value.StoredRows)
            + (value.FrameCount == 0 ? "" : " · původně " + value.MinimumOriginalCount + "–" + value.MaximumOriginalCount + " procesů ve snímku")
            + "\nSkutečně uchovaný rozsah: monotónní " + ProcessHistoryText.Mono(value.FirstRetainedNs) + " až " + ProcessHistoryText.Mono(value.LastRetainedNs)
            + " · " + value.RetainedFrames + " snímků / " + value.RetainedRows + " řádků · odhad uchovaných dat s rezervou " + Display.Number(value.AccountedBytes / 1048576d, " / 4 MiB", 2)
            + "\nVytěsněno celých snímků: " + value.EvictedFrames + " · jejich řádků: " + value.EvictedRows;
        precision.Text = value.Notice + "\nKaždý předaný nový procesový rámec se ukládá jako union top 8 CPU / RAM / I/O. Grafy mohou obsahovat další sběry, které sem nebyly předané."
            + "\nZjištěné mezery: " + value.GapCount + " · známé vynechané revize předání: " + value.KnownMissingRevisions
            + (value.DeliverySequenceUnknown ? " · úplnost číslování předání nelze určit" : "") + " · úplnost původního sběru nelze potvrdit"
            + (value.Gaps.Count == 0 ? "" : "\n" + string.Join(" · ", value.Gaps.Take(4).Select(gap => ProcessHistoryText.Mono(gap.BeforeNs) + " → " + ProcessHistoryText.Mono(gap.AfterNs)))
                + (value.GapCount > 4 ? " · další mezery jsou zahrnuté v počtu" : ""));
        ShowRows();
    }
    private void ShowRows()
    {
        rows.Replace(selection.Rows(metric));
        ToolTipService.SetToolTip(list, selection.Point ? "Všechny zachycené řádky nejbližšího snímku." : "Nejvýše 64 pozorovaných maxim zvolené metriky; každý řádek si zachovává skutečný čas a souběžné hodnoty.");
        foreach (var (kind, button) in metricButtons) button.Background = Display.Brush(kind == metric ? 0x28343bu : 0x1d252au);
        if (rows.Count > 0) list.SelectedIndex = 0; else detail.Text = "Pro tento výběr nejsou zachycené platné hodnoty zvolené metriky.";
    }
    private static TextBlock Text(string value) => new() { Text = value, FontFamily = UiTypography.Font, FontSize = 12, Foreground = Display.Brush(0x95a4ad), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static Button Command(string text) => new() { Content = text, FontFamily = UiTypography.Font, FontSize = 12, MinHeight = 0, Height = 32, Padding = new Thickness(12, 0, 12, 0), Background = Display.Brush(0x1d252a) };
}
