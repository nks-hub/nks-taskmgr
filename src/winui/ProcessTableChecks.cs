using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;
using Windows.Foundation;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static T? Find<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T wanted) return wanted;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); ++i)
            if (Find<T>(VisualTreeHelper.GetChild(element, i)) is T found) return found;
        return null;
    }

    private static void AlignedColumns(ProcessTable table, ListViewItem item)
    {
        var row = Find<ProcessRowGrid>(item) ?? throw new InvalidOperationException("Process row template was not realized.");
        Require(ReferenceEquals(row.Columns, table.Columns), "A realized process row lost the shared column model.");
        Require(Math.Abs(row.ActualWidth - table.Columns.LayoutWidth) < 1, "A realized process row has a different layout width from its header.");
        foreach (var column in table.Columns.Items) {
            var value = (FrameworkElement)row.Children[column.Id]; var heading = (FrameworkElement)table.Header.Children[column.Id];
            int position = Grid.GetColumn(value);
            Require(position == Grid.GetColumn(heading), "Column heading and value have different positions.");
            Require(Math.Abs(row.ColumnDefinitions[position].ActualWidth - table.Header.ColumnDefinitions[position].ActualWidth) < .1,
                "Column heading and value have different widths.");
            Require(value.Visibility == (column.Visible ? Visibility.Visible : Visibility.Collapsed), "Column visibility did not reach the row.");
            if (!column.Visible) continue;
            double valueRight = value.TransformToVisual(table).TransformPoint(new Point(value.ActualWidth + value.Margin.Right, 0)).X;
            double headerRight = heading.TransformToVisual(table).TransformPoint(new Point(heading.ActualWidth, 0)).X;
            Require(Math.Abs(valueRight - headerRight) < 1,
                $"Actual process column {column.Id} is shifted: valueRight={valueRight:F2};headerRight={headerRight:F2};rowWidth={row.ActualWidth:F2}.");
            if (column.Id == 0) {
                double valueLeft = value.TransformToVisual(table).TransformPoint(new Point(0, 0)).X - value.Margin.Left;
                double headerLeft = heading.TransformToVisual(table).TransformPoint(new Point(0, 0)).X;
                Require(Math.Abs(valueLeft - headerLeft) < 1, "Actual name-column indentation shifted the column boundary.");
            }
        }
        var name = row.Children[0] as ProcessNameCell ?? throw new InvalidOperationException("The process row lost its tree/name cell.");
        var source = item.Content as ProcessSlot ?? throw new InvalidOperationException("The realized process row lost its source slot.");
        Require(ReferenceEquals(name.Row, source.Row) && name.Margin == source.Row.Indent, "A recycled process name retained another row or depth.");
        Require(name.NameLabel.Text == source.Row.Name + (source.Row.Ended ? " · ukončeno" : source.Row.Stale ? " · zastaralé" : ""),
            "A recycled process label retained another name or status.");
        Require(name.Branch.Opacity == (source.Row.HasChildren ? 1 : 0), "The tree chevron does not match its actual branch.");
        Require(name.Branch.TransformToVisual(row).TransformPoint(new Point(name.Branch.ActualWidth, 0)).X
            < name.Icon.TransformToVisual(row).TransformPoint(new Point(0, 0)).X, "The tree chevron is not before the process icon.");
    }

    private static int AlignedRealizedRows(ProcessTable table, ListView list)
    {
        int checkedRows = 0;
        for (int i = 0; i < list.Items.Count; i++) {
            if (list.ContainerFromIndex(i) is not ListViewItem { IsLoaded: true } item) continue;
            AlignedColumns(table, item); ++checkedRows;
        }
        Require(checkedRows > 0, "No actual process rows were realized for alignment checks.");
        return checkedRows;
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
                var panel = list.ItemsPanelRoot as ItemsStackPanel
                    ?? throw new InvalidOperationException("Process table lost its virtualizing ItemsStackPanel.");
                int viewportRows = Math.Max(1, (int)Math.Ceiling(list.ActualHeight / ProcessTable.RowHeight));
                // The panel keeps a viewport-sized cache, so a fixed row-count limit fails on larger windows.
                int realizationLimit = (int)Math.Ceiling(viewportRows * (1 + panel.CacheLength)) + 8;
                Require(realized > 0 && realized <= realizationLimit && realized < replay.Count / 10,
                    $"Process table did not virtualize 10,000 replay rows: realized={realized};limit={realizationLimit};viewport={list.ActualHeight};cache={panel.CacheLength}.");
                var last = list.ContainerFromIndex(replay.Count - 1) as ListViewItem;
                Require(last is { IsLoaded: true }, "Process table did not scroll to the final replay row.");
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
                App.Trace($"Process table replay passed: {realized}/10000 realized rows; moved/resized/hidden columns; multiple selection");
            } finally {
                table.Width = originalWidth; table.HorizontalScroll.ChangeView(0, null, null, true);
                table.Columns.Reset(); list.ItemsSource = source;
                foreach (var row in selection) if (list.Items.Contains(row)) list.SelectedItems.Add(row);
            }
        };
    }
}
