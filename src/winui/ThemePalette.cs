using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace NksTaskMgr.WinUI;

internal static class ThemePalette
{
    private static readonly Dictionary<uint, SolidColorBrush> brushes = [];

    internal static SolidColorBrush Brush(uint color)
    {
        if (!brushes.TryGetValue(color, out var brush)) {
            brush = new SolidColorBrush(Color(color));
            brushes.Add(color, brush);
        }
        return brush;
    }

    internal static Windows.UI.Color Color(uint color, byte alpha = 255)
    {
        uint value = Appearance.IsDark ? color : color switch {
            0x12181c => 0xedf1f4, 0x171d21 => 0xf5f7f9, 0x1d252a => 0xffffff,
            0x222d34 => 0xe9eff3, 0x232d34 => 0xe9eff3, 0x253039 => 0xdce6ec,
            0x263138 => 0xe6ebef, 0x28343b => 0xdce7ee, 0x2c363d => 0xd5dde3,
            0x31494b => 0xd7eeeb, 0x33414a => 0xcddce6, 0x3b3024 => 0xfae8ce,
            0x3b4851 => 0xc5cfd6, 0x52616a => 0x8d9da8, 0x95a4ad => 0x5e707d,
            0xd7dfe3 => 0x26333c, 0xe9eef0 => 0x1b2832, 0xedc08b => 0x8a5214,
            0xef835f => 0xaa3818, 0x80c5c0 => 0x177e79, 0x83bca0 => 0x2a815d,
            0xabb0f1 => 0x6158b3, 0xcc9bb6 => 0x9d4278, 0xdfaf76 => 0x916224,
            _ => color
        };
        return Windows.UI.Color.FromArgb(alpha, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    internal static void Refresh()
    {
        foreach (var (color, brush) in brushes) brush.Color = Color(color);
    }

    internal static void AddResources(ResourceDictionary resources)
    {
        resources["NksDividerBrush"] = Brush(0x2c363d);
        resources["NksSecondaryTextBrush"] = Brush(0x95a4ad);
        resources["NksServiceBadgeBrush"] = Brush(0x222d34);
        resources["NksTransmitBrush"] = Brush(0x80c5c0);
        resources["NksGripBrush"] = Brush(0x3b4851);
    }
}
