using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private bool? compactSidebarActions;

    private void ResizeSidebarActions(bool compact)
    {
        if (compactSidebarActions == compact) return;
        updatesButton.Content = compact ? new SymbolIcon(Symbol.Download) { FontSize = 16 } : "Aktualizace";
        appearance.Content = compact ? new SymbolIcon(Symbol.Setting) { FontSize = 16 } : "Vzhled";
        compactSidebarActions = compact;
    }
}
