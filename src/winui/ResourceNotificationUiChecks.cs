using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private async Task CheckResourceContextAndPreviewAsync()
    {
        await CheckResourceContextMenuAsync();
        const long time = 10_000_000_000;
        var receive = new Metric { Value = 1_000_000, State = SampleState.Valid };
        var send = new Metric { Value = 2_000_000, State = SampleState.Valid };
        var row = new ResourceRow("preview-bindings", ResourceKind.Network);
        row.Update("Preview before", "Adapter before", "Připojeno", receive, send, default, 100_000_000, time);
        var probe = new ListView { ItemTemplate = resourceList.ItemTemplate, ItemsSource = new[] { row },
            Width = 300, Height = 100, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumnSpan(probe, Math.Max(1, performancePage.ColumnDefinitions.Count));
        Canvas.SetZIndex(probe, 100);
        int notifications = 0;
        System.ComponentModel.PropertyChangedEventHandler changed = (_, _) => ++notifications;
        row.PropertyChanged += changed;
        performancePage.Children.Add(probe);
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        async Task Rendered() { await NextRenderedFrame(); await NextRenderedFrame(); probe.UpdateLayout(); }
        try {
            await Rendered();
            var container = probe.ContainerFromItem(row) as ListViewItem
                ?? throw new InvalidOperationException("The resource preview probe has no realized item.");
            var labels = PreviewElements(container).OfType<StackPanel>()
                .Select(panel => panel.Children.OfType<TextBlock>().ToArray())
                .Single(items => items.Length == 3 && items[0].Text == row.Title);
            var template = VisualTreeHelper.GetParent(labels[0].Parent) as Grid
                ?? throw new InvalidOperationException("The resource preview probe has no production template grid.");
            var paths = PreviewElements(template).OfType<Microsoft.UI.Xaml.Shapes.Path>().ToArray();
            Check(paths.Length == 3, "The resource preview template lost a graph path.");
            string[] ReadTexts() => labels.Select(label => label.Text).ToArray();
            void Fresh()
            {
                var tooltip = ToolTipService.GetToolTip(template);
                string? description = tooltip is ToolTip control ? control.Content as string : tooltip as string;
                Check(ReadTexts().SequenceEqual(new[] { row.Title, row.Description, row.Value }) && description == row.Description
                    && ReferenceEquals(paths[0].Data, row.PreviewFill) && ReferenceEquals(paths[1].Data, row.Preview)
                    && ReferenceEquals(paths[2].Data, row.UploadPreview) && ReferenceEquals(probe.ContainerFromItem(row), container),
                    "The production resource template retained stale labels, tooltip or graphs, or replaced its container.");
            }
            Fresh();
            var originalText = ReadTexts(); var originalGeometry = row.Preview;
            row.RefreshPreview(); row.RefreshPreview();
            Check(notifications == 0, "An unchanged bound resource still notified its template.");
            row.Update("Preview after", "Adapter after", "Připojeno", receive with { Value = 5_000_000 },
                send with { Value = 7_000_000 }, default, 100_000_000, time + 1_000_000_000, notify: false);
            await Rendered();
            Check(notifications == 0 && originalText.SequenceEqual(ReadTexts()) && ReferenceEquals(row.Preview, originalGeometry),
                "A hidden resource update published bindings before its preview refresh.");
            row.RefreshPreview(); await Rendered(); Fresh();
            Check(notifications == 1 && !ReferenceEquals(row.Preview, originalGeometry), "A resource return did not publish fresh bindings exactly once.");
            row.RefreshPreview(); Check(notifications == 1, "A duplicate resource return notified its template again.");
            row.SetStale(true); row.RefreshPreview(); await Rendered(); Fresh();
            Check(notifications == 2 && labels[2].Text == "Zastaralá data", "A stale resource changed more than its value notification or kept old text.");
            row.SetStale(false, notify: false); row.RefreshPreview(); await Rendered(); Fresh();
            Check(notifications == 3, "A deferred stale-state return did not update its bound value.");
            originalGeometry = row.Preview;
            row.History.Append(time + 2_000_000_000, row.Primary); row.RefreshPreview(); await Rendered(); Fresh();
            Check(notifications == 4 && !ReferenceEquals(row.Preview, originalGeometry), "A graph-only change failed to refresh its bound geometry.");
            row.RefreshPreview(); Check(notifications == 4, "An unchanged final resource state notified again.");
            App.Trace("Resource notification UI passed: production template; three labels, tooltip and three graph bindings; unchanged/deferred/stale/graph-only updates; stable container");
        } finally {
            row.PropertyChanged -= changed;
            performancePage.Children.Remove(probe);
        }
    }

    private static IEnumerable<DependencyObject> PreviewElements(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
            foreach (var child in PreviewElements(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
