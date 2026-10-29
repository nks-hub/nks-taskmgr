using Microsoft.UI.Xaml.Media;

namespace NksTaskMgr.WinUI;

internal static class UiTypography
{
    public static FontFamily Font { get; } = new(File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "SegUIVar.ttf"))
        ? "Segoe UI Variable" : "Segoe UI");
}
