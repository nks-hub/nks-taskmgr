using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private Task? smokeCompactShell;

    /// <summary>Checks the high contrast role mapping and, when Windows high contrast is on, the live sidebar colors.</summary>
    private void CheckHighContrast()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        Require(ThemePalette.HighContrastRole(0x12181c) == UIElementType.Window && ThemePalette.HighContrastRole(0x1d252a) == UIElementType.Window
            && ThemePalette.HighContrastRole(0xe9eef0) == UIElementType.WindowText && ThemePalette.HighContrastRole(0x95a4ad) == UIElementType.WindowText
            && ThemePalette.HighContrastRole(0x2c363d) == UIElementType.WindowText && ThemePalette.HighContrastRole(0x28343b) == UIElementType.Highlight
            && ThemePalette.HighContrastRole(0xedc08b) == UIElementType.Hotlight, "High contrast roles map a surface, text, line, selection or series wrongly.");
        if (!Appearance.IsHighContrast) {
            Require(Display.Brush(ThemePalette.SelectedLabel).Color == Display.Brush(0x95a4ad).Color, "The selected label changed the normal palette.");
            App.Trace("High contrast roles passed; Windows high contrast is off, normal palette unchanged");
            return;
        }
        Windows.UI.Color Of(Brush? brush) => (brush as SolidColorBrush)?.Color ?? default;
        var window = Appearance.SystemColor(UIElementType.Window);
        var selected = new[] { processesNav, applicationNav, performanceNav, servicesNav, usersNav, hardwareNav, startupNav }
            .Single(button => Of(button.Background) == Appearance.SystemColor(UIElementType.Highlight));
        var label = ((Panel)selected.Content).Children.OfType<TextBlock>().Single();
        Require(Of(sidebar.Background) == window, "The sidebar ignores the high contrast window color.");
        Require(Of(label.Foreground) == Appearance.SystemColor(UIElementType.HighlightText), "The selected navigation label ignores HighlightText.");        App.Trace($"High contrast passed: window={window}, selected navigation '{label.Text}' on Highlight with HighlightText");
    }

    /// <summary>Lays out every main view in a 1000×700 DIP window and rejects clipped sidebar labels or overlapping header parts.</summary>
    private async Task CheckCompactShellAsync()
    {
        if (!AppWindow.InsideWorkArea())
            throw new InvalidOperationException($"The initial window {AppWindow.Position.X},{AppWindow.Position.Y} {AppWindow.Size.Width}×{AppWindow.Size.Height} leaves the monitor work area.");
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
        IEnumerable<ButtonBase> Buttons(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) yield break;
            if (node is ButtonBase button) { yield return button; yield break; }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); ++i)
                foreach (var child in Buttons(VisualTreeHelper.GetChild(node, i))) yield return child;
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
                foreach (var button in Buttons(page).Concat(Buttons(shellHeader)))
                    if (button.ActualWidth > 0 && button.Content is not (string or TextBlock) && string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
                        throw new InvalidOperationException($"{next}: a {button.GetType().Name} with {button.Content?.GetType().Name} content has no accessible name.");
                ++views;
            }
            App.Trace($"Compact shell passed: {views} views at {root.ActualWidth:F0}×{root.ActualHeight:F0} DIP, scale={scale:F2}, theme={(Appearance.IsDark ? "dark" : "light")}, {labels} sidebar labels unclipped, header parts disjoint");
        } finally {
            AppWindow.Resize(original); ShowView(originalView);
        }
    }
}
