using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private void CheckPerformanceViewport(bool requireScrollbar = false)
    {
        if (performanceDetailScroll.ActualWidth <= 0 || performanceBody.ActualWidth <= 0)
            throw new InvalidOperationException("Performance viewport has no measured width.");
        var body = PerformanceBounds(performanceBody);
        double safeRight = performanceDetailScroll.ActualWidth - PerformanceScrollGutter;
        if (performanceDetailScroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled
            || performanceDetailScroll.Padding.Right < 16 || body.Right > safeRight + 1
            || Math.Abs(performanceDetailWidth - performanceBody.ActualWidth) > 1)
            throw new InvalidOperationException("Performance layout did not reserve its measured scrollbar gutter.");

        var elements = new List<FrameworkElement> {
            chartGrid, facts, performanceFacts, consumers, chart, processors, coreOverlay, pageFile, pageFileTitle,
            plotToolbar, chartControls, cpuModes, resourceTitle, resourceDescription, resourceEyebrow, resourceValue, resourceUnit,
            timelineSelection, historicalProcessesButton, diskTransferPanel, diskTransfers, gpuPanel, gpuEngines,
            gpuMemory, gpuDedicated, gpuShared
        };
        elements.AddRange(coreCharts.Values);
        foreach (var tile in gpuTiles) { elements.Add(tile.Choice); elements.Add(tile.Value); elements.Add(tile.Chart); }
        elements.AddRange(consumerItems.Select(item => item.Button));
        int measured = 0, measuredPlots = 0;
        foreach (var element in elements) {
            if (!VisibleWithinPerformance(element)) continue;
            if (element is ChartView) {
                if (element.ActualWidth <= 0) throw new InvalidOperationException("A visible performance graph has no measured width.");
                ++measuredPlots;
            }
            if (element.ActualWidth <= 0) continue;
            var bounds = PerformanceBounds(element);
            if (bounds.Left < body.Left - 1 || bounds.Right > safeRight + 1)
                throw new InvalidOperationException($"Performance content extends into the scrollbar gutter: {element.GetType().Name};left={bounds.Left:F2};right={bounds.Right:F2};safeRight={safeRight:F2} DIP.");
            ++measured;
        }
        if (measuredPlots == 0) throw new InvalidOperationException("No visible performance graph was measured.");

        var pending = new Stack<DependencyObject>(); pending.Push(performanceDetailScroll);
        double scrollbarWidth = 0;
        while (pending.Count > 0) {
            var node = pending.Pop();
            if (node is FrameworkElement { Name: "VerticalScrollBar", Visibility: Visibility.Visible, ActualWidth: > 0, ActualHeight: > 0 } bar
                && NativePerformanceClass(bar) == "Microsoft.UI.Xaml.Controls.Primitives.ScrollBar" && OwnsPerformanceScrollbar(bar)) {
                var bounds = PerformanceBounds(bar); scrollbarWidth = Math.Max(scrollbarWidth, bounds.Width);
                if (body.Right > bounds.Left + 1) throw new InvalidOperationException("Performance body overlaps its actual vertical scrollbar bounds.");
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); ++i) pending.Push(VisualTreeHelper.GetChild(node, i));
        }
        if (requireScrollbar && (performanceDetailScroll.ScrollableHeight <= 0 || scrollbarWidth <= 0))
            throw new InvalidOperationException(FormattableString.Invariant($"The scrollbar overlap check did not exercise a scrollable viewport: actual={performanceDetailScroll.ActualHeight:F2};viewport={performanceDetailScroll.ViewportHeight:F2};extent={performanceDetailScroll.ExtentHeight:F2};scrollable={performanceDetailScroll.ScrollableHeight:F2};scrollbar={scrollbarWidth:F2} DIP."));
        App.Trace($"Performance viewport passed: content={performanceBody.ActualWidth:F2};gutter={PerformanceScrollGutter};scrollbar={scrollbarWidth:F2};elements={measured};plots={measuredPlots};units=DIP");
    }

    private void CheckScrollablePerformanceViewport()
    {
        double maximum = performanceDetailScroll.MaxHeight, offset = performanceDetailScroll.VerticalOffset;
        try {
            performanceDetailScroll.MaxHeight = 250;
            root.UpdateLayout();
            CheckPerformanceViewport(requireScrollbar: true);
        } finally {
            performanceDetailScroll.MaxHeight = maximum;
            root.UpdateLayout();
            performanceDetailScroll.ChangeView(null, offset, null, true);
        }
    }

    private Rect PerformanceBounds(FrameworkElement element) => element.TransformToVisual(performanceDetailScroll)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static string NativePerformanceClass(DependencyObject element)
        => new WinRT.IInspectable(((WinRT.IWinRTObject)element).NativeObject).GetRuntimeClassName(false);

    private bool OwnsPerformanceScrollbar(FrameworkElement bar)
    {
        // Native AOT can project template ScrollBar and ScrollViewer nodes as Control.
        for (var parent = VisualTreeHelper.GetParent(bar); parent is not null; parent = VisualTreeHelper.GetParent(parent)) {
            if (parent is UIElement { Visibility: Visibility.Collapsed }) return false;
            if (ReferenceEquals(parent, performanceDetailScroll)) return true;
            if (NativePerformanceClass(parent) == "Microsoft.UI.Xaml.Controls.ScrollViewer") return false;
        }
        return false;
    }

    private bool VisibleWithinPerformance(FrameworkElement element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node)) {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
            if (ReferenceEquals(node, performanceDetailScroll)) return true;
        }
        return false;
    }
}
