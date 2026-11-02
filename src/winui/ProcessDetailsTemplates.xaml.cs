using Microsoft.UI.Xaml;

namespace NksTaskMgr.WinUI;

public sealed partial class ProcessDetailsTemplates : ResourceDictionary
{
    public ProcessDetailsTemplates() { InitializeComponent(); }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ProcessDetailsRow
{
    internal object Source { get; }
    private string? text;
    public string Text => text ??= Source.ToString()!;
    internal ProcessDetailsRow(object source) { Source = source; }
}
