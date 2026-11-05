using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private Task? smokeCompactShell;

    /// <summary>Lays out every main view in a 1000×700 DIP window and rejects clipped sidebar labels or overlapping header parts.</summary>
    private async Task CheckCompactShellAsync()
    {
        var original = AppWindow.Size; var originalView = view;
        double scale = root.XamlRoot?.RasterizationScale ?? 1;
        Rect Bounds(FrameworkElement element) => element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        static bool Overlap(Rect a, Rect b) => a.Width > 0 && b.Width > 0 && a.Left < b.Right - 1 && b.Left < a.Right - 1 && a.Top < b.Bottom - 1 && b.Top < a.Bottom - 1;
        IEnumerable<TextBlock> Texts(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) yield break;
            if (node is TextBlock text) yield return text;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); ++i)
                foreach (var child in Texts(VisualTreeHelper.GetChild(node, i))) yield return child;
        }
        int views = 0, labels = 0;
        try {
            AppWindow.Resize(new SizeInt32((int)Math.Round(1000 * scale), (int)Math.Round(700 * scale)));
            foreach (var next in new[] { DesktopView.Processes, DesktopView.Applications, DesktopView.Performance, DesktopView.Services,
                DesktopView.Users, DesktopView.Hardware, DesktopView.Startup }) {
                ShowView(next);
                await NextRenderedFrame(); await NextRenderedFrame(); root.UpdateLayout();
                if (Math.Abs(root.ActualWidth - 1000) > 40) throw new InvalidOperationException($"Compact shell check got width {root.ActualWidth:F1} DIP instead of about 1000.");
                var header = Bounds(shellHeader);
                var tools = (FrameworkElement)shellHeader.Children[1];
                var parts = new[] { heading, overviewScroll, tools }.Where(part => part.Visibility == Visibility.Visible && part.ActualWidth > 0).Select(Bounds).ToArray();
                for (int i = 0; i < parts.Length; ++i) {
                    if (parts[i].Left < header.Left - 1 || parts[i].Right > header.Right + 1)
                        throw new InvalidOperationException($"{next}: header part {i} leaves the header ({parts[i].Left:F1}–{parts[i].Right:F1} of {header.Left:F1}–{header.Right:F1} DIP).");
                    for (int j = i + 1; j < parts.Length; ++j)
                        if (Overlap(parts[i], parts[j])) throw new InvalidOperationException($"{next}: header parts {i} and {j} overlap.");
                }
                foreach (var tool in new FrameworkElement[] { search, interval, pause }.Where(tool => tool.Visibility == Visibility.Visible))
                    if (Bounds(tool).Right > header.Right + 1) throw new InvalidOperationException($"{next}: header tool {tool.GetType().Name} is cut off.");
                var side = Bounds(sidebar);
                foreach (var text in Texts(sidebar).Where(text => text.ActualWidth > 0 && text.Text.Length > 0)) {
                    var bounds = Bounds(text);
                    if (text.IsTextTrimmed || bounds.Left < side.Left - 1 || bounds.Right > side.Right + 1)
                        throw new InvalidOperationException($"{next}: sidebar label '{text.Text}' is clipped.");
                    ++labels;
                }
                if (Bounds(page).Right > root.ActualWidth + 1) throw new InvalidOperationException($"{next}: page content is wider than the window.");
                ++views;
            }
            App.Trace($"Compact shell passed: {views} views at {root.ActualWidth:F0}×{root.ActualHeight:F0} DIP, scale={scale:F2}, {labels} sidebar labels unclipped, header parts disjoint");
        } finally {
            AppWindow.Resize(original); ShowView(originalView);
        }
    }
}
