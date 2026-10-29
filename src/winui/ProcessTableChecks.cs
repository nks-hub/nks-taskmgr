using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;

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
        foreach (var column in table.Columns.Items) {
            int position = Grid.GetColumn((FrameworkElement)row.Children[column.Id]);
            Require(position == Grid.GetColumn((FrameworkElement)table.Header.Children[column.Id]), "Column heading and value have different positions.");
            Require(Math.Abs(row.ColumnDefinitions[position].ActualWidth - table.Header.ColumnDefinitions[position].ActualWidth) < .1,
                "Column heading and value have different widths.");
            Require(row.Children[column.Id].Visibility == (column.Visible ? Visibility.Visible : Visibility.Collapsed), "Column visibility did not reach the row.");
        }
    }

    public static Action BeginProcessTableReplay(ProcessTable table, ListView list)
    {
        var source = list.ItemsSource;
        var selection = list.SelectedItems.Cast<ProcessSlot>().ToArray();
        table.Columns.MoveTo(2, 0); table.Columns.Resize(2, 137); table.Columns.SetVisible(5, false);
        list.UpdateLayout();
        Require(selection.SequenceEqual(list.SelectedItems.Cast<ProcessSlot>()), "Changing columns lost process selection.");
        var first = Enumerable.Range(0, list.Items.Count).Select(index => list.ContainerFromIndex(index)).OfType<ListViewItem>().First();
        AlignedColumns(table, first);
        var replay = new List<ProcessSlot>();
        for (uint i = 1; i <= 10_000; ++i) {
            var row = new ProcessRow(new ProcessKey(new(1, 2), i, GenerationKind.CreationTime, i));
            row.Update(new ProcessInfo { CpuPercent = new Metric { State = SampleState.Valid, Value = i % 100 } }); replay.Add(new ProcessSlot(row));
        }
        list.ItemsSource = replay; list.UpdateLayout();
        list.SelectedItem = replay[^1]; list.SelectedItems.Add(replay[^2]);
        list.ScrollIntoView(replay[^1]);
        return () => {
            try {
                int realized = Enumerable.Range(0, replay.Count).Count(index => list.ContainerFromIndex(index) is not null);
                Require(realized > 0 && realized < 100, "Process table did not virtualize 10,000 replay rows.");
                var last = list.ContainerFromIndex(replay.Count - 1) as ListViewItem;
                Require(last is not null, "Process table did not scroll to the final replay row.");
                Require(list.SelectedItems.Contains(replay[^1]) && list.SelectedItems.Contains(replay[^2]), "Replay scrolling lost multiple selection.");
                AlignedColumns(table, last!);
                table.Columns.Move(2, 1); table.Columns.SetVisible(5, true); table.Columns.Resize(2, -1); list.UpdateLayout();
                AlignedColumns(table, last!);
                Require(table.Columns.WidthOf(2) >= 70, "Resizing passed the CPU column minimum.");
                App.Trace($"Process table replay passed: {realized}/10000 realized rows; moved/resized/hidden columns; multiple selection");
            } finally {
                table.Columns.Reset(); list.ItemsSource = source;
                foreach (var row in selection) if (list.Items.Contains(row)) list.SelectedItems.Add(row);
            }
        };
    }
}
