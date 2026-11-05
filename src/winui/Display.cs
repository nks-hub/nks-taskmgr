using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;
using Windows.Graphics;

namespace NksTaskMgr.WinUI;

public static partial class Display
{
    private static readonly CultureInfo Czech = CultureInfo.GetCultureInfo("cs-CZ");
    public static string Number(double value, string unit, int digits = 1) => value.ToString("N" + digits, Czech) + unit;
    public static string Metric(Metric value, string unit, double divisor = 1) => value.State == SampleState.Valid
        ? Number(value.Value / divisor, unit) : value.State switch {
            SampleState.First => "První vzorek", SampleState.Stale => "Zastaralá data",
            SampleState.AccessDenied => "Chybí oprávnění", SampleState.Gap => "Mezera", _ => "Měření nedostupné"
        };
    public static string Bytes(ulong value) => value switch {
        >= 1UL << 40 => Number(value / 1099511627776d, " TiB", 2),
        >= 1UL << 30 => Number(value / 1073741824d, " GiB", 2),
        >= 1UL << 20 => Number(value / 1048576d, " MiB", 1),
        >= 1UL << 10 => Number(value / 1024d, " KiB", 1),
        _ => value.ToString("N0", Czech) + " B"
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
        window.Resize(new SizeInt32(Math.Min((int)Math.Round(width * scale), area.Width), Math.Min((int)Math.Round(height * scale), area.Height)));
    }
}
