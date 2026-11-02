using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;
using Windows.Foundation;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    internal static async Task ProcessTableLifecycleAsync(Window window, ProcessTable table, ListView list)
    {
        WindowOwnership.Minimize(window);
        Require(WindowOwnership.IsMinimized(window), "The process-table lifetime check did not minimize its window.");
        await Task.Delay(150);
        WindowOwnership.Restore(window); window.Activate();
        Require(!WindowOwnership.IsMinimized(window), "The process-table lifetime check did not restore its window.");
        double width = table.Columns.WidthOf(2);
        table.Columns.Resize(2, width + 1); table.Columns.Resize(2, width);
        var rendered = new TaskCompletionSource(); int frames = 0;
        void Frame(object? sender, object args) { if (++frames == 2) rendered.TrySetResult(); }
        CompositionTarget.Rendering += Frame;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= Frame; }
        table.UpdateLayout(); list.UpdateLayout();
        try {
            int rows = AlignedRealizedRows(table, list);
            App.Trace($"Process table lifetime passed: minimized=True;restored=True;columnChange=True;actualX=True;rows={rows}");
        } catch (InvalidOperationException error) when (error.Message == ProcessBindingSourceError) {
            try { await ProbeProcessTableBindingAsync(table, list, error); }
            catch (Exception probeError) { TraceProcessBindingProbeError(error, "probe", probeError); }
            throw;
        }
    }

    private static T? Find<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T wanted) return wanted;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); ++i)
            if (Find<T>(VisualTreeHelper.GetChild(element, i)) is T found) return found;
        return null;
    }

    private static bool ProcessRowVisible(ListView list, FrameworkElement element)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        var bounds = element.TransformToVisual(list).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        if (bounds.Right <= 0 || bounds.Bottom <= 0 || bounds.Left >= list.ActualWidth || bounds.Top >= list.ActualHeight) return false;
        for (DependencyObject? ancestor = element; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private static bool AlignedColumns(ProcessTable table, ListView list, ListViewItem item, int loopIndex)
    {
        object? content = item.Content;
        var row = Find<ProcessRowGrid>(item) ?? throw new InvalidOperationException("Process row template was not realized.");
        void Check(bool condition, string message) {
            if (!condition) TraceColumnMismatch(table, list, row, item, content, loopIndex, message);
            Require(condition, message);
        }
        Check(ReferenceEquals(row.Columns, table.Columns), "A realized process row lost the shared column model.");
        bool visible = ProcessRowVisible(list, row);
        Check(Math.Abs(row.Width - table.Columns.LayoutWidth) < 1, "A realized process row has a different intended width from its header.");
        if (visible) Check(Math.Abs(row.ActualWidth - table.Columns.LayoutWidth) < 1, "A visible process row has a different layout width from its header.");
        foreach (var column in table.Columns.Items) {
            var value = (FrameworkElement)row.Children[column.Id]; var heading = (FrameworkElement)table.Header.Children[column.Id];
            int position = Grid.GetColumn(value);
            Check(position == Grid.GetColumn(heading), "Column heading and value have different positions: id=" + column.Id);
            double expectedWidth = table.Columns.WidthOf(column.Id);
            Check(row.ColumnDefinitions[position].Width.GridUnitType == GridUnitType.Pixel
                && table.Header.ColumnDefinitions[position].Width.GridUnitType == GridUnitType.Pixel
                && Math.Abs(row.ColumnDefinitions[position].Width.Value - expectedWidth) < .1
                && Math.Abs(table.Header.ColumnDefinitions[position].Width.Value - expectedWidth) < .1,
                "Column heading or value has a different intended width from the shared model.");
            if (visible) Check(Math.Abs(row.ColumnDefinitions[position].ActualWidth - table.Header.ColumnDefinitions[position].ActualWidth) < .1,
                "Visible column heading and value have different widths.");
            Check(value.Visibility == (column.Visible ? Visibility.Visible : Visibility.Collapsed), "Column visibility did not reach the row.");
            if (!column.Visible || !visible) continue;
            double valueRight = value.TransformToVisual(table).TransformPoint(new Point(value.ActualWidth + value.Margin.Right, 0)).X;
            double headerRight = heading.TransformToVisual(table).TransformPoint(new Point(heading.ActualWidth, 0)).X;
            Check(Math.Abs(valueRight - headerRight) < 1,
                $"Actual process column {column.Id} is shifted: valueRight={valueRight:F2};headerRight={headerRight:F2};rowWidth={row.ActualWidth:F2}.");
            if (column.Id == 0) {
                double valueLeft = value.TransformToVisual(table).TransformPoint(new Point(0, 0)).X - value.Margin.Left;
                double headerLeft = heading.TransformToVisual(table).TransformPoint(new Point(0, 0)).X;
                Check(Math.Abs(valueLeft - headerLeft) < 1, "Actual name-column indentation shifted the column boundary.");
            }
        }
        var name = row.Children[0] as ProcessNameCell;
        if (name is null) {
            TraceColumnMismatch(table, list, row, item, content, loopIndex, "The process row lost its tree/name cell.");
            throw new InvalidOperationException("The process row lost its tree/name cell.");
        }
        var source = content as ProcessSlot;
        if (source is null) {
            TraceColumnMismatch(table, list, row, item, content, loopIndex, "The realized process row lost its source slot.");
            throw CaptureProcessTableBindingFailure(list, item, loopIndex);
        }
        Check(ReferenceEquals(name.Row, source.Row) && name.Margin == source.Row.Indent, "A recycled process name retained another row or depth.");
        Check(name.NameLabel.Text == source.Row.Name + (source.Row.Ended ? " · ukončeno" : source.Row.Stale ? " · zastaralé" : ""),
            "A recycled process label retained another name or status.");
        Check(name.Branch.Opacity == (source.Row.HasChildren ? 1 : 0), "The tree chevron does not match its actual branch.");
        Check(Grid.GetColumn(name.Branch) < Grid.GetColumn(name.Icon), "The intended tree chevron column is not before the process icon.");
        if (visible) Check(name.Branch.TransformToVisual(row).TransformPoint(new Point(name.Branch.ActualWidth, 0)).X
            < name.Icon.TransformToVisual(row).TransformPoint(new Point(0, 0)).X, "The visible tree chevron is not before the process icon.");
        return visible;
    }

    private static void TraceColumnMismatch(ProcessTable table, ListView list, ProcessRowGrid row, ListViewItem item, object? content, int loopIndex, string reason)
    {
        static string TypeName(object? value)
        {
            if (value is null) return "<null>";
            try { return value.GetType().AssemblyQualifiedName ?? value.GetType().FullName ?? "<type name unavailable>"; }
            catch (Exception error) { return "<type error: " + error.GetType().Name + ">"; }
        }
        var slot = content as ProcessSlot;
        int containerIndex = list.IndexFromContainer(item);
        object? expectedItem = containerIndex >= 0 && containerIndex < list.Items.Count ? list.Items[containerIndex] : null;
        object? canonicalItem = list.ItemFromContainer(item), itemsSource = list.ItemsSource;
        object? itemContext = item.DataContext, rowContext = row.DataContext;
        var name = row.Children.Count > 0 ? row.Children[0] as ProcessNameCell : null;
        object? nameContext = name?.DataContext;
        var expectedSlot = expectedItem as ProcessSlot;
        App.Trace($"table-binding loopIndex={loopIndex};containerIndex={containerIndex};contentNull={content is null};contentType={TypeName(content)};itemsSourceType={TypeName(itemsSource)};expectedType={TypeName(expectedItem)};expectedKey={expectedSlot?.Row.Key};canonicalType={TypeName(canonicalItem)};contentExpected={ReferenceEquals(content, expectedItem)};canonicalExpected={ReferenceEquals(canonicalItem, expectedItem)};contentCanonical={ReferenceEquals(content, canonicalItem)};itemContextType={TypeName(itemContext)};rowContextType={TypeName(rowContext)};nameContextType={TypeName(nameContext)};contentItemContext={ReferenceEquals(content, itemContext)};contentRowContext={ReferenceEquals(content, rowContext)};contentNameContext={ReferenceEquals(content, nameContext)};itemContextExpected={ReferenceEquals(itemContext, expectedItem)};rowContextExpected={ReferenceEquals(rowContext, expectedItem)};nameContextExpected={ReferenceEquals(nameContext, expectedItem)};nameSlotNull={name?.Slot is null};nameSlotType={TypeName(name?.Slot)};nameSlotKey={name?.Slot?.Row.Key};nameRowKey={name?.Row?.Key};nameSlotExpected={ReferenceEquals(name?.Slot, expectedItem)};nameRowExpected={ReferenceEquals(name?.Row, expectedSlot?.Row)}");
        string viewport;
        try {
            var bounds = item.TransformToVisual(list).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            var rowBounds = row.TransformToVisual(list).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
            bool intersects = bounds.Right > 0 && bounds.Bottom > 0 && bounds.Left < list.ActualWidth && bounds.Top < list.ActualHeight;
            bool effectiveVisible = true; DependencyObject? ancestor = row;
            for (int i = 0; ancestor is not null && i < 128; ++i, ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is UIElement { Visibility: Visibility.Collapsed }) effectiveVisible = false;
            viewport = $"itemBounds={bounds};rowBounds={rowBounds};listWidth={list.ActualWidth:F2};listHeight={list.ActualHeight:F2};inViewport={intersects};effectiveVisible={effectiveVisible};calculatedOffscreen={!intersects || !effectiveVisible}";
        } catch (Exception error) { viewport = "boundsError=" + error; }
        App.Trace($"table-layout reason={reason};key={slot?.Row.Key};rowLoaded={row.IsLoaded};itemLoaded={item.IsLoaded};subscribed={row.ColumnsSubscribed};loads={row.LoadCount};unloads={row.UnloadCount};attaches={row.AttachCount};detaches={row.DetachCount};applies={row.ApplyCount};revision={table.Columns.Revision};appliedRevision={row.LastAppliedRevision};rowColumnsNull={row.Columns is null};rowColumnsShared={ReferenceEquals(row.Columns, table.Columns)};slotColumnsNull={slot?.Columns is null};slotColumnsShared={ReferenceEquals(slot?.Columns, table.Columns)};dataContextSlot={ReferenceEquals(rowContext, slot)};rowWidth={row.ActualWidth:F2};explicitRowWidth={row.Width:F2};layoutWidth={table.Columns.LayoutWidth:F2};headerWidth={table.Header.ActualWidth:F2};tableWidth={table.ActualWidth:F2};horizontalOffset={table.HorizontalScroll.HorizontalOffset:F2};rowChildren={row.Children.Count};headerChildren={table.Header.Children.Count};lastPositions={string.Join(",", row.LastAppliedPositions)};{viewport}");
        int expectedPosition = 0;
        foreach (var column in table.Columns.Items) {
            var value = (FrameworkElement)row.Children[column.Id]; var heading = (FrameworkElement)table.Header.Children[column.Id];
            int position = Grid.GetColumn(value), headerPosition = Grid.GetColumn(heading);
            App.Trace($"table-column id={column.Id};expected={expectedPosition++};rowPosition={position};headerPosition={headerPosition};visible={column.Visible};rowVisibility={value.Visibility};cellType={value.GetType().FullName};cellWidth={value.ActualWidth:F2};headingWidth={heading.ActualWidth:F2};rowDefinitionWidth={(position < row.ColumnDefinitions.Count ? row.ColumnDefinitions[position].ActualWidth : double.NaN):F2};headerDefinitionWidth={(headerPosition < table.Header.ColumnDefinitions.Count ? table.Header.ColumnDefinitions[headerPosition].ActualWidth : double.NaN):F2};modelWidth={table.Columns.WidthOf(column.Id):F2}");
        }
    }

    private static int AlignedRealizedRows(ProcessTable table, ListView list)
    {
        int checkedRows = 0, visibleRows = 0;
        for (int i = 0; i < list.Items.Count; i++) {
            if (list.ContainerFromIndex(i) is not ListViewItem { IsLoaded: true } item) continue;
            if (AlignedColumns(table, list, item, i)) ++visibleRows;
            ++checkedRows;
        }
        Require(checkedRows > 0, "No actual process rows were realized for alignment checks.");
        Require(visibleRows > 0, "No process rows were visible for actual-coordinate alignment checks.");
        App.Trace($"Process table rows checked: realized={checkedRows};visible={visibleRows};cached={checkedRows - visibleRows}");
        return visibleRows;
    }

    public static Action BeginProcessTableReplay(ProcessTable table, ListView list)
    {
        var source = list.ItemsSource;
        var selection = list.SelectedItems.Cast<ProcessSlot>().ToArray();
        double originalWidth = table.Width;
        table.Columns.MoveTo(2, 0); table.Columns.Resize(2, 137); table.Columns.SetVisible(5, false);
        list.UpdateLayout();
        Require(selection.SequenceEqual(list.SelectedItems.Cast<ProcessSlot>()), "Changing columns lost process selection.");
        AlignedRealizedRows(table, list);
        var replay = new List<ProcessSlot>();
        int branchClicks = 0; ProcessRow? toggled = null;
        void ToggleBranch(ProcessRow row) { ++branchClicks; toggled = row; row.SetTree(row.Depth, row.HasChildren, !row.Collapsed); }
        for (uint i = 1; i <= 10_000; ++i) {
            var row = new ProcessRow(new ProcessKey(new(1, 2), i, GenerationKind.CreationTime, i));
            row.Update(new ProcessInfo { CpuPercent = new Metric { State = SampleState.Valid, Value = i % 100 } });
            row.SetTree((int)(i % 4), i % 5 == 0, i % 10 == 0);
            replay.Add(new ProcessSlot(row) { Columns = table.Columns, ToggleBranch = ToggleBranch });
        }
        table.Width = Math.Max(480, Math.Min(700, table.ActualWidth - 200));
        list.ItemsSource = replay; table.UpdateLayout(); list.UpdateLayout();
        list.SelectedItem = replay[^1]; list.SelectedItems.Add(replay[^2]);
        list.ScrollIntoView(replay[^1]);
        table.HorizontalScroll.ChangeView(table.HorizontalScroll.ScrollableWidth, null, null, true);
        return () => {
            try {
                int realized = Enumerable.Range(0, replay.Count).Count(index => list.ContainerFromIndex(index) is not null);
                var rawPanel = list.ItemsPanelRoot;
                string runtimeClass = "null";
                if (rawPanel is not null) {
                    try { runtimeClass = new WinRT.IInspectable(((WinRT.IWinRTObject)rawPanel).NativeObject).GetRuntimeClassName(false); }
                    catch (Exception error) { runtimeClass = $"error:{error.GetType().Name}:{error.HResult:X8}"; }
                }
                if (runtimeClass != "Microsoft.UI.Xaml.Controls.ItemsStackPanel") {
                    string bounds = "null";
                    if (rawPanel is not null) {
                        try { bounds = rawPanel.TransformToVisual(list).TransformBounds(new Rect(0, 0, rawPanel.ActualWidth, rawPanel.ActualHeight)).ToString(); }
                        catch (Exception error) { bounds = $"error:{error.GetType().Name}:{error.HResult:X8}"; }
                    }
                    App.Trace(FormattableString.Invariant($"table-panel rawNull={rawPanel is null};rawType={rawPanel?.GetType().AssemblyQualifiedName};runtimeClass={runtimeClass};panelLoaded={rawPanel?.IsLoaded};panelBounds={bounds};panelWidth={rawPanel?.ActualWidth};panelHeight={rawPanel?.ActualHeight};listLoaded={list.IsLoaded};listWidth={list.ActualWidth};listHeight={list.ActualHeight};items={list.Items.Count};replay={replay.Count};realized={realized};itemsSourceType={list.ItemsSource?.GetType().AssemblyQualifiedName}"));
                    throw new InvalidOperationException("Process table lost its virtualizing ItemsStackPanel.");
                }
                // NativeAOT can expose the actual ItemsStackPanel through its base Panel projection.
                object? cacheValue = rawPanel!.GetValue(ItemsStackPanel.CacheLengthProperty);
                if (cacheValue is not double cacheLength || !double.IsFinite(cacheLength) || cacheLength < 0)
                    throw new InvalidOperationException($"The actual ItemsStackPanel cache is invalid: type={cacheValue?.GetType().AssemblyQualifiedName};value={cacheValue}.");
                int viewportRows = Math.Max(1, (int)Math.Ceiling(list.ActualHeight / ProcessTable.RowHeight));
                // The panel keeps a viewport-sized cache, so a fixed row-count limit fails on larger windows.
                int realizationLimit = (int)Math.Ceiling(viewportRows * (1 + cacheLength)) + 8;
                Require(realized > 0 && realized <= realizationLimit && realized < replay.Count / 10,
                    $"Process table did not virtualize 10,000 replay rows: realized={realized};limit={realizationLimit};viewport={list.ActualHeight};cache={cacheLength}.");
                var last = list.ContainerFromIndex(replay.Count - 1) as ListViewItem;
                Require(last is { IsLoaded: true } && Find<ProcessRowGrid>(last) is { } lastRow && ProcessRowVisible(list, lastRow),
                    "Process table did not visibly scroll to the final replay row.");
                Require(list.SelectedItems.Contains(replay[^1]) && list.SelectedItems.Contains(replay[^2]), "Replay scrolling lost multiple selection.");
                Require(table.HorizontalScroll.HorizontalOffset > 0, "The actual horizontal-scrolling alignment scenario did not scroll.");
                int aligned = AlignedRealizedRows(table, list);
                var tree = Find<ProcessNameCell>(last!) ?? throw new InvalidOperationException("The actual final replay row lost its tree control.");
                new ButtonAutomationPeer(tree.Branch).Invoke();
                Require(branchClicks == 1 && ReferenceEquals(toggled, replay[^1].Row) && !replay[^1].Row.Collapsed
                    && (tree.Branch.Content as Microsoft.UI.Xaml.Shapes.Polyline)?.RenderTransform is RotateTransform { Angle: 90 },
                    "The actual chevron did not expand its current source row and glyph.");
                new ButtonAutomationPeer(tree.Branch).Invoke();
                Require(branchClicks == 2 && replay[^1].Row.Collapsed, "The actual chevron did not restore its collapsed state.");
                table.Width = originalWidth; table.UpdateLayout(); list.UpdateLayout();
                table.Columns.Move(2, 1); table.Columns.SetVisible(5, true); table.Columns.Resize(2, -1); list.UpdateLayout();
                AlignedRealizedRows(table, list);
                Require(table.Columns.WidthOf(2) >= 70, "Resizing passed the CPU column minimum.");
                App.Trace($"Process table actual alignment passed: rows={aligned};sharedColumns=True;actualX=True;horizontalScroll=True;resize=True;recycledTree=True;chevron=True");
                App.Trace(FormattableString.Invariant($"Process table replay passed: {realized}/10000 realized rows; moved/resized/hidden columns; multiple selection;runtimeClass={runtimeClass};cache={cacheLength};limit={realizationLimit}"));
            } finally {
                table.Width = originalWidth; table.HorizontalScroll.ChangeView(0, null, null, true);
                table.Columns.Reset(); list.ItemsSource = source;
                foreach (var row in selection) if (list.Items.Contains(row)) list.SelectedItems.Add(row);
            }
        };
    }
}
