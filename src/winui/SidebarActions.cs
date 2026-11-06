using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private bool? compactSidebarActions;

    private void ResizeSidebarActions(bool compact)
    {
        if (compactSidebarActions == compact) return;
        updatesButton.Content = compact ? SidebarIcon(Symbol.Download) : Loc.T("Aktualizace");
        appearance.Content = compact ? SidebarIcon(Symbol.Setting) : Loc.T("Vzhled");
        compactSidebarActions = compact;
    }

    private static Viewbox SidebarIcon(Symbol symbol) => new() {
        Width = 16, Height = 16, Stretch = Stretch.Uniform, Child = new SymbolIcon(symbol)
    };
}
