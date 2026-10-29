using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace NksTaskMgr.WinUI;

public sealed partial class ProcessTable : Grid
{
    private readonly Grid content = new(), header = new() { Background = Display.Brush(0x1d252a), Padding = new Thickness(16, 0, 16, 0) };
    private readonly ScrollViewer scroll;
    private readonly Dictionary<int, Button> headings = [];
    private int? draggedColumn;
    private int sortColumn;
    private bool descending;
    public ProcessColumns Columns { get; } = new();
    internal Grid Header => header;

    public ProcessTable(ListView rows, Action<int> sort)
    {
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(39) });
        content.RowDefinitions.Add(new RowDefinition());
        foreach (var column in Columns.Items) {
            var cell = new Grid();
            var button = new Button {
                Content = column.Title, FontSize = 12, FontFamily = UiTypography.Font, Height = 38, MinHeight = 0, Padding = new Thickness(0, 0, 12, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = column.Id == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Background = Display.Brush(0x1d252a), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), CanDrag = true, AllowDrop = true
            };
            AutomationProperties.SetAutomationId(button, "process-column-" + column.Id);
            button.Click += (_, _) => sort(column.Id);
            ToolTipService.SetToolTip(button, "Kliknutím seřadit, přetažením přesunout. Pravé tlačítko otevře další volby.");
            button.ContextFlyout = ColumnMenu(column);
            button.DragStarting += (_, args) => { draggedColumn = column.Id; args.Data.SetText(column.Title); args.Data.RequestedOperation = DataPackageOperation.Move; };
            button.DropCompleted += (_, _) => draggedColumn = null;
            button.DragOver += (_, args) => { args.AcceptedOperation = draggedColumn.HasValue ? DataPackageOperation.Move : DataPackageOperation.None; args.Handled = true; };
            button.Drop += (_, args) => {
                if (draggedColumn is int id) { Columns.MoveTo(id, column.Id); args.AcceptedOperation = DataPackageOperation.Move; }
                draggedColumn = null; args.Handled = true;
            };
            headings.Add(column.Id, button); cell.Children.Add(button);
            var area = new ColumnResizeArea { Width = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var grip = new Thumb { Style = (Style)Application.Current.Resources["ColumnGripStyle"] }; area.Children.Add(grip);
            AutomationProperties.SetName(grip, "Šířka sloupce " + column.Title);
            double initial = 0, delta = 0;
            grip.DragStarted += (_, _) => { initial = Columns.WidthOf(column.Id); delta = 0; };
            grip.DragDelta += (_, args) => { delta += args.HorizontalChange; Columns.Resize(column.Id, initial + delta); };
            cell.Children.Add(area); header.Children.Add(cell);
        }
        content.Children.Add(header); Grid.SetRow(rows, 1); content.Children.Add(rows);
        ScrollViewer.SetHorizontalScrollBarVisibility(rows, ScrollBarVisibility.Disabled);
        scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SizeChanged += (_, args) => Columns.SetViewport(args.NewSize.Width);
        Columns.Changed += Apply;
        Children.Add(scroll); Apply(); SetSort(0, false);
    }

    private MenuFlyout ColumnMenu(ProcessColumn column)
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, Action action) {
            var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => action(); menu.Items.Add(item); return item;
        }
        var left = Item("Přesunout doleva", () => Columns.Move(column.Id, -1));
        var right = Item("Přesunout doprava", () => Columns.Move(column.Id, 1));
        menu.Items.Add(new MenuFlyoutSeparator());
        var narrower = Item("Zúžit o 20 bodů", () => Columns.Resize(column.Id, Columns.WidthOf(column.Id) - 20));
        var wider = Item("Rozšířit o 20 bodů", () => Columns.Resize(column.Id, Columns.WidthOf(column.Id) + 20));
        if (column.Id != 0) { menu.Items.Add(new MenuFlyoutSeparator()); Item("Skrýt sloupec", () => Columns.SetVisible(column.Id, false)); }
        menu.Opening += (_, _) => {
            left.IsEnabled = Columns.CanMove(column.Id, -1); right.IsEnabled = Columns.CanMove(column.Id, 1);
            narrower.IsEnabled = Columns.WidthOf(column.Id) > column.Minimum; wider.IsEnabled = Columns.WidthOf(column.Id) < (column.Id == 0 ? 1000 : 500);
        };
        return menu;
    }

    public MenuFlyout VisibilityMenu()
    {
        var menu = new MenuFlyout();
        foreach (var column in Columns.Items.OrderBy(column => column.Id)) {
            var item = new ToggleMenuFlyoutItem { Text = column.Title, IsEnabled = column.Id != 0 };
            item.Click += (_, _) => Columns.SetVisible(column.Id, item.IsChecked);
            menu.Opening += (_, _) => item.IsChecked = column.Visible; menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var reset = new MenuFlyoutItem { Text = "Obnovit výchozí sloupce" }; reset.Click += (_, _) => Columns.Reset(); menu.Items.Add(reset);
        return menu;
    }

    public void SetSort(int id, bool reverse)
    {
        sortColumn = id; descending = reverse;
        foreach (var column in Columns.Items) {
            var button = headings[column.Id];
            button.Content = column.Title + (column.Id == id ? reverse ? " ↓" : " ↑" : "");
            AutomationProperties.SetName(button, column.Title + (column.Id == id ? reverse ? ", sestupně" : ", vzestupně" : ", seřadit"));
        }
    }

    private void Apply()
    {
        content.Width = Math.Max(scroll.ActualWidth, Columns.MinimumWidth);
        Columns.Apply(header);
        string? hiddenSort = Columns.Items.FirstOrDefault(column => column.Id == sortColumn && !column.Visible)?.Title;
        AutomationProperties.SetHelpText(this, hiddenSort is null ? "" : "Řazeno podle skrytého sloupce " + hiddenSort + (descending ? " sestupně" : " vzestupně"));
    }
}

public sealed partial class ProcessRowGrid : Grid
{
    private ProcessTable? table;
    public ProcessRowGrid()
    {
        Loaded += (_, _) => {
            DependencyObject? parent = VisualTreeHelper.GetParent(this);
            while (parent is not null && parent is not ProcessTable) parent = VisualTreeHelper.GetParent(parent);
            table = parent as ProcessTable;
            if (table is not null) { table.Columns.Changed += Apply; Apply(); }
        };
        Unloaded += (_, _) => { if (table is not null) table.Columns.Changed -= Apply; table = null; };
    }
    private void Apply() => table?.Columns.Apply(this);
}

internal sealed partial class ColumnResizeArea : Grid
{
    public ColumnResizeArea() => ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
}
