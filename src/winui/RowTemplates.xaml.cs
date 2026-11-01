using Microsoft.UI.Xaml;

namespace NksTaskMgr.WinUI;

public sealed partial class RowTemplates : ResourceDictionary
{
    public RowTemplates() { InitializeComponent(); ThemePalette.AddResources(this); }
}
