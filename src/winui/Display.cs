using System.Globalization;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static class Display
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
}

