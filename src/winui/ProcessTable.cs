using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace NksTaskMgr.WinUI;

public sealed partial class ProcessTable : Grid
{
    internal const double RowHeight = 28;
    private readonly Grid content = new(), header = new() { Background = Display.Brush(0x1d252a), Padding = new Thickness(16, 0, 16, 0) };
    private readonly ScrollViewer scroll;
    private readonly Dictionary<int, Button> headings = [];
    private int? draggedColumn;
    private int sortColumn;
    private bool descending, branchValues;
    public ProcessColumns Columns { get; } = new();
    internal Grid Header => header;
    internal ScrollViewer HorizontalScroll => scroll;

    public ProcessTable(ListView rows, Action<int> sort)
    {
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        content.RowDefinitions.Add(new RowDefinition());
        foreach (var column in Columns.Items) {
            var cell = new Grid();
            var button = new Button {
                Content = column.Title, FontSize = 12, FontFamily = UiTypography.Font, Height = 31, MinHeight = 0, Padding = new Thickness(0, 0, 12, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = column.Id == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Background = Display.Brush(0x1d252a), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), CanDrag = true, AllowDrop = true
            };
            AutomationProperties.SetAutomationId(button, "process-column-" + column.Id);
            button.Click += (_, _) => sort(column.Id);
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
        ScrollViewer.SetHorizontalScrollMode(rows, ScrollMode.Disabled);
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
        UpdateHeadings();
    }

    internal void SetBranchValues(bool value)
    {
        if (branchValues == value) return;
        branchValues = value; UpdateHeadings();
    }

    private void UpdateHeadings()
    {
        foreach (var column in Columns.Items) {
            var button = headings[column.Id];
            bool metric = column.Id >= 2;
            string scope = metric ? branchValues ? ", proces s potomky" : ", vlastní hodnoty" : "";
            button.Content = column.Title + (column.Id == sortColumn ? descending ? " ↓" : " ↑" : "");
            AutomationProperties.SetName(button, column.Title + scope
                + (column.Id == sortColumn ? descending ? ", sestupně" : ", vzestupně" : ", seřadit"));
            string help = "Kliknutím seřadit, přetažením přesunout. Pravé tlačítko otevře další volby.";
            if (metric) help += branchValues ? " Hodnoty procesu a ověřených potomků; chybějící údaje jsou označené." : " Vlastní hodnota procesu bez potomků.";
            if (branchValues && column.Id == 3) help += " Sdílené stránky RAM se mohou započítat opakovaně.";
            ToolTipService.SetToolTip(button, help);
        }
    }

    private void Apply()
    {
        content.Width = Columns.LayoutWidth;
        Columns.Apply(header);
        string? hiddenSort = Columns.Items.FirstOrDefault(column => column.Id == sortColumn && !column.Visible)?.Title;
        AutomationProperties.SetHelpText(this, hiddenSort is null ? "" : "Řazeno podle skrytého sloupce " + hiddenSort + (descending ? " sestupně" : " vzestupně"));
    }
}

public sealed partial class ProcessRowGrid : Grid
{
    private ProcessColumns? columns;
    private bool subscribed, unloadCheckQueued;
    internal bool ColumnsSubscribed => subscribed;
    internal int LoadCount { get; private set; }
    internal int UnloadCount { get; private set; }
    internal int AttachCount { get; private set; }
    internal int DetachCount { get; private set; }
    internal int ApplyCount { get; private set; }
    internal int LastAppliedRevision { get; private set; } = -1;
    internal int[] LastAppliedPositions { get; private set; } = [];
    public ProcessColumns? Columns {
        get => columns;
        set {
            if (ReferenceEquals(columns, value)) return;
            Detach(); columns = value;
            if (IsLoaded) Attach();
            Apply();
        }
    }

    public ProcessRowGrid()
    {
        Height = ProcessTable.RowHeight;
        Loaded += (_, _) => { ++LoadCount; Attach(); Apply(); };
        Unloaded += (_, _) => { ++UnloadCount; CheckAfterUnload(); };
    }
    // Recycling can deliver Unloaded while the template is already loaded again.
    private void CheckAfterUnload()
    {
        if (unloadCheckQueued) return;
        unloadCheckQueued = true;
        if (!DispatcherQueue.TryEnqueue(() => {
            unloadCheckQueued = false;
            if (IsLoaded) { Attach(); Apply(); } else Detach();
        })) { unloadCheckQueued = false; Detach(); }
    }
    private void Attach()
    {
        if (subscribed || columns is null) return;
        columns.Changed += Apply; subscribed = true; ++AttachCount;
    }
    private void Detach()
    {
        if (subscribed && columns is not null) { columns.Changed -= Apply; ++DetachCount; }
        subscribed = false;
    }
    private void Apply()
    {
        if (columns is null || Children.Count == 0) return;
        Width = columns.LayoutWidth;
        if (Children.Count != columns.Items.Count) {
            if (IsLoaded) throw new InvalidOperationException("Procesní řádek nemá všechny buňky sdílených sloupců.");
            return;
        }
        columns.Apply(this);
        ++ApplyCount; LastAppliedRevision = columns.Revision;
        if (LastAppliedPositions.Length != columns.Items.Count) LastAppliedPositions = new int[columns.Items.Count];
        foreach (var column in columns.Items) LastAppliedPositions[column.Id] = GetColumn((FrameworkElement)Children[column.Id]);
    }
}

public sealed partial class ProcessNameCell : Grid
{
    private readonly TextBlock name = new() { FontFamily = UiTypography.Font, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly ProcessIconView icon = new() { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
    private readonly RotateTransform rotation = new() { CenterX = 6, CenterY = 6 };
    private readonly Button branch = new() { Width = 14, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
        BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), IsTabStop = false, VerticalAlignment = VerticalAlignment.Center };
    private ProcessSlot? slot;
    private ProcessRow? row;
    private bool subscribed, unloadCheckQueued;
    public ProcessSlot? Slot {
        get => slot;
        set {
            if (ReferenceEquals(slot, value)) return;
            Detach(); slot = value; RefreshRow();
        }
    }
    internal ProcessRow? Row => row;
    internal Button Branch => branch;
    internal ProcessIconView Icon => icon;
    internal TextBlock NameLabel => name;

    public ProcessNameCell()
    {
        ColumnSpacing = 6; VerticalAlignment = VerticalAlignment.Center;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        ColumnDefinitions.Add(new ColumnDefinition());
        branch.Content = new Polyline { Points = new PointCollection { new Point(3, 2), new Point(7, 6), new Point(3, 10) },
            Width = 12, Height = 12, Stroke = Display.Brush(0x95a4ad), StrokeThickness = 1.4, RenderTransform = rotation };
        branch.Click += (_, _) => { if (row is { HasChildren: true, Ended: false } current) slot?.ToggleBranch?.Invoke(current); };
        branch.DoubleTapped += (_, args) => args.Handled = true;
        Children.Add(branch); Children.Add(icon); Children.Add(name); SetColumn(icon, 1); SetColumn(name, 2);
        Loaded += (_, _) => { Detach(); RefreshRow(); };
        Unloaded += (_, _) => CheckAfterUnload();
        SizeChanged += (_, args) => Clip = new RectangleGeometry { Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
    }

    private void CheckAfterUnload()
    {
        if (unloadCheckQueued) return;
        unloadCheckQueued = true;
        if (!DispatcherQueue.TryEnqueue(() => {
            unloadCheckQueued = false;
            if (IsLoaded) { Detach(); RefreshRow(); } else Detach();
        })) { unloadCheckQueued = false; Detach(); }
    }

    private void Attach()
    {
        if (subscribed || slot is null) return;
        slot.PropertyChanged += SlotChanged;
        if (row is not null) row.PropertyChanged += Changed;
        subscribed = true;
    }
    private void Detach()
    {
        if (subscribed) {
            if (slot is not null) slot.PropertyChanged -= SlotChanged;
            if (row is not null) row.PropertyChanged -= Changed;
        }
        subscribed = false;
    }
    private void SlotChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!string.IsNullOrEmpty(args.PropertyName) && args.PropertyName != nameof(ProcessSlot.Row)) return;
        Detach(); RefreshRow();
    }
    private void RefreshRow()
    {
        row = slot?.Row; icon.Row = row;
        if (IsLoaded) Attach();
        Update();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName is nameof(ProcessRow.DisplayName) or nameof(ProcessRow.Indent) or nameof(ProcessRow.Note)) Update();
    }
    private void Update()
    {
        Margin = row?.Indent ?? new Thickness(0);
        name.Text = row is null ? "" : row.Name + (row.Ended ? " · ukončeno" : row.Stale ? " · zastaralé" : "");
        branch.Opacity = row?.HasChildren == true ? 1 : 0;
        branch.IsHitTestVisible = branch.IsEnabled = row is { HasChildren: true, Ended: false };
        rotation.Angle = row?.Collapsed == true ? 0 : 90;
        AutomationProperties.SetName(branch, (row?.Collapsed == true ? "Rozbalit " : "Sbalit ") + row?.Name);
        ToolTipService.SetToolTip(this, row?.Note);
    }
}

internal sealed partial class ColumnResizeArea : Grid
{
    public ColumnResizeArea() => ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
}
