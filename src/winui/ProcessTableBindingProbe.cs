using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;
using System.Runtime.CompilerServices;
using Windows.Foundation;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private const string ProcessBindingSourceError = "The realized process row lost its source slot.";
    private const string ProcessBindingTargetKey = "ProcessTableBindingTarget";
    private sealed record ProcessBindingTarget(ProcessSlot Slot, ProcessRow Row, ProcessKey Key, ulong Created100ns,
        int Index, ListViewItem Container, object? ItemsSource);
    private readonly record struct ProcessBindingLayout(ListViewItem Container, Rect Bounds, double VerticalOffset,
        double HorizontalOffset, double Viewport, double Extent, double ListWidth, double ListHeight, double RowWidth,
        double IntendedWidth, int Revision);

    private static InvalidOperationException CaptureProcessTableBindingFailure(ListView list, ListViewItem item, int index)
    {
        var error = new InvalidOperationException(ProcessBindingSourceError);
        try {
            int actualIndex = list.IndexFromContainer(item);
            if (list.ItemFromContainer(item) is ProcessSlot slot && actualIndex == index && index >= 0
                && index < list.Items.Count && ReferenceEquals(list.Items[index], slot))
                error.Data[ProcessBindingTargetKey] = new ProcessBindingTarget(slot, slot.Row, slot.Row.Key,
                    slot.Row.Created100ns, index, item, list.ItemsSource);
            else TraceProcessBindingProbe(error, $"table-binding-probe outcome=inconclusive;reason=no-exact-canonical-slot;index={index};containerIndex={actualIndex}");
        } catch (Exception captureError) { TraceProcessBindingProbeError(error, "capture", captureError); }
        return error;
    }

    private static async Task ProbeProcessTableBindingAsync(ProcessTable table, ListView list, InvalidOperationException original)
    {
        if (original.Data[ProcessBindingTargetKey] is not ProcessBindingTarget target) return;
        bool SameIdentity() => ReferenceEquals(list.ItemsSource, target.ItemsSource) && list.Items.Contains(target.Slot)
            && ReferenceEquals(target.Slot.Row, target.Row) && target.Row.Key == target.Key && target.Row.Created100ns == target.Created100ns;
        void Inconclusive() => TraceProcessBindingProbe(original, $"table-binding-probe outcome=inconclusive;reason=identity-changed;originalKey={target.Key};currentKey={target.Slot.Row.Key};originalCreated100ns={target.Created100ns};currentCreated100ns={target.Slot.Row.Created100ns};rowRef={ReferenceEquals(target.Slot.Row, target.Row)};sourceRef={ReferenceEquals(list.ItemsSource, target.ItemsSource)};slotPresent={list.Items.Contains(target.Slot)}");

        if (!SameIdentity()) { Inconclusive(); return; }
        TraceProcessBindingState(table, list, target.Container, target, original, "before");
        var scroll = Find<ScrollViewer>(list) ?? throw new InvalidOperationException("The binding probe has no process scroll viewer.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessBindingLayout? previous = null;
        bool changing = false;
        int stableFrames = 0, viewVersion = 0, lastViewVersion = -1, finalViews = 0, contentEvents = 0;
        double initialOffset = scroll.VerticalOffset;
        const int contentLogLimit = 20;
        void Changing(object? sender, ScrollViewerViewChangingEventArgs args) { changing = true; ++viewVersion; stableFrames = 0; }
        void Changed(object? sender, ScrollViewerViewChangedEventArgs args) {
            changing = args.IsIntermediate; ++viewVersion; stableFrames = 0;
            if (!args.IsIntermediate) ++finalViews;
        }
        void ContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) {
            try {
                if (!ReferenceEquals(args.Item, target.Slot) && !ReferenceEquals(args.ItemContainer, target.Container)) return;
                if (++contentEvents <= contentLogLimit)
                    TraceProcessBindingProbe(original, $"table-binding-probe ccc={contentEvents};phase={args.Phase};inRecycleQueue={args.InRecycleQueue};index={args.ItemIndex};itemRef={ReferenceEquals(args.Item, target.Slot)};originalContainerRef={ReferenceEquals(args.ItemContainer, target.Container)};itemId={ProcessBindingRef(args.Item)};containerId={ProcessBindingRef(args.ItemContainer)}");
            } catch (Exception eventError) { ready.TrySetException(eventError); }
        }
        void Frame(object? sender, object args) {
            try {
                if (!SameIdentity()) { ready.TrySetResult(); return; }
                if (list.ActualWidth <= 0 || list.ActualHeight <= 0 || scroll.ViewportHeight <= 0
                    || !double.IsFinite(scroll.VerticalOffset) || !double.IsFinite(scroll.ViewportHeight) || !double.IsFinite(scroll.ExtentHeight)
                    || list.ContainerFromItem(target.Slot) is not ListViewItem { IsLoaded: true } item
                    || Find<ProcessRowGrid>(item) is not { } row || !ProcessRowVisible(list, row)) {
                    stableFrames = 0; previous = null; return;
                }
                var bounds = row.TransformToVisual(list).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
                if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)) {
                    stableFrames = 0; previous = null; return;
                }
                var layout = new ProcessBindingLayout(item, bounds, scroll.VerticalOffset, table.HorizontalScroll.HorizontalOffset,
                    scroll.ViewportHeight, scroll.ExtentHeight, list.ActualWidth, list.ActualHeight, row.ActualWidth,
                    table.Columns.LayoutWidth, table.Columns.Revision);
                bool stable = !changing && (scroll.VerticalOffset == initialOffset || finalViews > 0)
                    && lastViewVersion == viewVersion && previous is { } last && layout == last;
                stableFrames = stable ? stableFrames + 1 : 0;
                previous = layout; lastViewVersion = viewVersion;
                if (stableFrames >= 2) ready.TrySetResult();
            } catch (Exception frameError) { ready.TrySetException(frameError); }
        }

        try {
            list.ContainerContentChanging += ContentChanging;
            scroll.ViewChanging += Changing; scroll.ViewChanged += Changed; CompositionTarget.Rendering += Frame;
            list.ScrollIntoView(target.Slot);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!SameIdentity()) { Inconclusive(); return; }
            var item = list.ContainerFromItem(target.Slot) as ListViewItem
                ?? throw new InvalidOperationException("The binding probe lost the exact item container.");
            var row = Find<ProcessRowGrid>(item) ?? throw new InvalidOperationException("The binding probe lost its row template.");
            Require(item.IsLoaded && ProcessRowVisible(list, row), "The binding probe did not reach the actual viewport.");
            bool payload = TraceProcessBindingState(table, list, item, target, original, "after");
            bool aligned = false;
            try { aligned = AlignedColumns(table, list, item, list.IndexFromContainer(item)); }
            catch (Exception alignmentError) { TraceProcessBindingProbeError(original, "visible-alignment", alignmentError); }
            if (!SameIdentity()) { Inconclusive(); return; }
            TraceProcessBindingProbe(original, $"table-binding-probe outcome={(payload && aligned ? "visible-pass" : "visible-fail")};payload={payload};originalAlignedColumns={aligned};sameIdentity=True;stableFrames={stableFrames};finalViews={finalViews};cccEvents={contentEvents};cccDropped={Math.Max(0, contentEvents - contentLogLimit)};originalFailureRetained=True");
        } catch (TimeoutException) when (!SameIdentity()) { Inconclusive(); }
        finally {
            list.ContainerContentChanging -= ContentChanging;
            scroll.ViewChanging -= Changing; scroll.ViewChanged -= Changed; CompositionTarget.Rendering -= Frame;
        }
    }

    private static bool TraceProcessBindingState(ProcessTable table, ListView list, ListViewItem item,
        ProcessBindingTarget target, InvalidOperationException original, string stage)
    {
        var row = Find<ProcessRowGrid>(item) ?? throw new InvalidOperationException("The binding snapshot has no row template.");
        var name = row.Children[0] as ProcessNameCell ?? throw new InvalidOperationException("The binding snapshot has no name cell.");
        var current = target.Slot.Row;
        int index = list.IndexFromContainer(item);
        object? canonical = list.ItemFromContainer(item);
        bool canonicalRef = ReferenceEquals(canonical, target.Slot) && index >= 0 && index < list.Items.Count
            && ReferenceEquals(list.Items[index], target.Slot);
        var bounds = row.TransformToVisual(list).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
        TraceProcessBindingProbe(original, FormattableString.Invariant($"table-binding-probe stage={stage};originalIndex={target.Index};index={index};key={current.Key};created100ns={current.Created100ns};slotId={ProcessBindingRef(target.Slot)};rowId={ProcessBindingRef(current)};containerId={ProcessBindingRef(item)};originalContainerRef={ReferenceEquals(item, target.Container)};rowRef={ReferenceEquals(current, target.Row)};canonicalRef={canonicalRef};contentNull={item.Content is null};contentRef={ReferenceEquals(item.Content, target.Slot)};itemContextRef={ReferenceEquals(item.DataContext, target.Slot)};rowContextRef={ReferenceEquals(row.DataContext, target.Slot)};nameContextRef={ReferenceEquals(name.DataContext, target.Slot)};itemContextNull={item.DataContext is null};rowContextNull={row.DataContext is null};nameContextNull={name.DataContext is null};nameSlotRef={ReferenceEquals(name.Slot, target.Slot)};nameRowRef={ReferenceEquals(name.Row, current)};loaded={item.IsLoaded};visible={ProcessRowVisible(list, row)};bounds={bounds.X},{bounds.Y},{bounds.Width},{bounds.Height};viewport={list.ActualWidth},{list.ActualHeight};columnsRef={ReferenceEquals(row.Columns, table.Columns)};slotColumnsRef={ReferenceEquals(target.Slot.Columns, table.Columns)};revision={table.Columns.Revision};appliedRevision={row.LastAppliedRevision};subscribed={row.ColumnsSubscribed}"));

        string[] expected = [current.Name + (current.Ended ? " · ukončeno" : current.Stale ? " · zastaralé" : ""),
            current.PidText, current.CpuText, current.WorkingText, current.PrivateText, current.ReadText, current.WriteText, current.NetworkText];
        bool payload = canonicalRef && ReferenceEquals(name.Slot, target.Slot) && ReferenceEquals(name.Row, current)
            && ReferenceEquals(row.Columns, table.Columns) && ReferenceEquals(target.Slot.Columns, table.Columns);
        foreach (var column in table.Columns.Items) {
            var cell = (FrameworkElement)row.Children[column.Id]; var heading = (FrameworkElement)table.Header.Children[column.Id];
            string? actual = column.Id == 0 ? name.NameLabel.Text : (cell as TextBlock)?.Text;
            bool textMatches = actual == expected[column.Id]; payload &= textMatches;
            int position = Grid.GetColumn(cell), headingPosition = Grid.GetColumn(heading);
            double valueRight = cell.TransformToVisual(table).TransformPoint(new Point(cell.ActualWidth + cell.Margin.Right, 0)).X;
            double headingRight = heading.TransformToVisual(table).TransformPoint(new Point(heading.ActualWidth, 0)).X;
            TraceProcessBindingProbe(original, FormattableString.Invariant($"table-binding-probe cell;stage={stage};id={column.Id};actual={ProcessBindingText(actual)};expected={ProcessBindingText(expected[column.Id])};textMatches={textMatches};position={position};headerPosition={headingPosition};visibility={cell.Visibility};expectedVisible={column.Visible};widthUnit={row.ColumnDefinitions[position].Width.GridUnitType};intendedWidth={row.ColumnDefinitions[position].Width.Value};modelWidth={table.Columns.WidthOf(column.Id)};actualWidth={row.ColumnDefinitions[position].ActualWidth};headerWidth={table.Header.ColumnDefinitions[headingPosition].ActualWidth};valueRight={valueRight};headerRight={headingRight}"));
        }
        bool rotated = (name.Branch.Content as Microsoft.UI.Xaml.Shapes.Polyline)?.RenderTransform is RotateTransform rotation
            && rotation.Angle == (current.Collapsed ? 0 : 90);
        bool branchEnabled = current.HasChildren && !current.Ended;
        double branchRight = name.Branch.TransformToVisual(row).TransformPoint(new Point(name.Branch.ActualWidth, 0)).X;
        double iconLeft = name.Icon.TransformToVisual(row).TransformPoint(new Point(0, 0)).X;
        bool tree = name.Margin == current.Indent && name.Branch.Opacity == (current.HasChildren ? 1 : 0)
            && name.Branch.IsEnabled == branchEnabled && name.Branch.IsHitTestVisible == branchEnabled && rotated
            && ReferenceEquals(name.Icon.Row, current) && Grid.GetColumn(name.Branch) < Grid.GetColumn(name.Icon)
            && (!ProcessRowVisible(list, row) || branchRight < iconLeft);
        TraceProcessBindingProbe(original, FormattableString.Invariant($"table-binding-probe tree;stage={stage};depth={current.Depth};indent={name.Margin};expectedIndent={current.Indent};hasChildren={current.HasChildren};collapsed={current.Collapsed};branchOpacity={name.Branch.Opacity};branchEnabled={name.Branch.IsEnabled};branchHitTest={name.Branch.IsHitTestVisible};rotationMatches={rotated};iconRowRef={ReferenceEquals(name.Icon.Row, current)};branchRight={branchRight};iconLeft={iconLeft};treeMatches={tree}"));
        return payload && tree;
    }

    private static int ProcessBindingRef(object? value) => value is null ? 0 : RuntimeHelpers.GetHashCode(value);
    private static string ProcessBindingText(string? value) => value is null ? "<null>"
        : value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace(";", "\\;");
    private static void TraceProcessBindingProbeError(InvalidOperationException original, string phase, Exception error)
    {
        original.Data["ProcessBindingProbeError:" + phase] = error.ToString();
        TraceProcessBindingProbe(original, $"table-binding-probe outcome=error;phase={phase};type={error.GetType().FullName};hresult={error.HResult:X8};error={ProcessBindingText(error.ToString())};originalFailureRetained=True");
    }
    private static void TraceProcessBindingProbe(InvalidOperationException original, string message)
    {
        // A diagnostic log failure must not replace the original binding exception.
        try { App.Trace(message); }
        catch (Exception logError) { original.Data["ProcessBindingProbeLogError"] = logError.ToString(); }
    }
}
