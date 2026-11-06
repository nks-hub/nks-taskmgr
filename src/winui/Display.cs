using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;
using Windows.Graphics;

namespace NksTaskMgr.WinUI;

public static partial class Display
{
    public static string Number(double value, string unit, int digits = 1) => value.ToString("N" + digits, Loc.Culture) + unit;
    public static string Metric(Metric value, string unit, double divisor = 1) => value.State == SampleState.Valid
        ? Number(value.Value / divisor, unit) : value.State switch {
            SampleState.First => Loc.T("První vzorek"), SampleState.Stale => Loc.T("Zastaralá data"),
            SampleState.AccessDenied => Loc.T("Chybí oprávnění"), SampleState.Gap => Loc.T("Mezera"), _ => Loc.T("Měření nedostupné")
        };
    public static string Bytes(ulong value) => value switch {
        >= 1UL << 40 => Number(value / 1099511627776d, " TiB", 2),
        >= 1UL << 30 => Number(value / 1073741824d, " GiB", 2),
        >= 1UL << 20 => Number(value / 1048576d, " MiB", 1),
        >= 1UL << 10 => Number(value / 1024d, " KiB", 1),
        _ => value.ToString("N0", Loc.Culture) + " B"
    };
    public static SolidColorBrush Brush(uint color) => ThemePalette.Brush(color);
    public static uint ResourceColor(ResourceKind kind) => kind switch {
        ResourceKind.Cpu => 0x80c5c0, ResourceKind.Memory => 0xabb0f1, ResourceKind.Disk => 0x83bca0,
        ResourceKind.Gpu => 0xcc9bb6, _ => 0xdfaf76
    };

    [LibraryImport("user32.dll")] private static partial uint GetDpiForWindow(nint window);

    /// <summary>Resizes a window to a size given in DIPs at the DPI of its monitor, kept inside the monitor work area.</summary>
    public static void ResizeDip(this AppWindow window, int width, int height)
    {
        uint dpi = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(window.Id));
        double scale = (dpi == 0 ? 96 : dpi) / 96d;
        var area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest).WorkArea;
        int w = Math.Min((int)Math.Round(width * scale), area.Width), h = Math.Min((int)Math.Round(height * scale), area.Height);
        // The default cascade position can push a clamped window past the right or bottom edge.
        var at = window.Position;
        window.MoveAndResize(new RectInt32(Math.Clamp(at.X, area.X, area.X + area.Width - w), Math.Clamp(at.Y, area.Y, area.Y + area.Height - h), w, h));
    }

    /// <summary>True when the window lies inside its monitor work area, allowing the invisible resize border.</summary>
    internal static bool InsideWorkArea(this AppWindow window)
    {
        var area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest).WorkArea;
        const int border = 12;
        return window.Position.X >= area.X - border && window.Position.Y >= area.Y - border
            && window.Position.X + window.Size.Width <= area.X + area.Width + border && window.Position.Y + window.Size.Height <= area.Y + area.Height + border;
    }
}
