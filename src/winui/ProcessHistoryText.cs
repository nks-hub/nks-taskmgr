using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static class ProcessHistoryText
{
    internal static string Time(long time, long wall)
        => wall > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(wall / 1_000_000).LocalDateTime.ToString("HH:mm:ss.fff") : Mono(time);
    internal static string Mono(long value) => value > 0 ? Display.Number(value / 1_000_000_000d, " s", 3) : "nedostupný";
    internal static string State(SampleState value) => value switch {
        SampleState.Valid => "platný", SampleState.First => "první vzorek", SampleState.Unavailable => "nedostupné", SampleState.AccessDenied => "přístup odmítnut",
        SampleState.Unsupported => "nepodporované", SampleState.Stale => "zastaralé", SampleState.Gap => "mezera", _ => "chyba"
    };
    private static string Interval(MeasurementMetadata? value) => value is not { } metadata ? "měřicí okno není uložené" : metadata.IntervalState switch {
        MeasurementIntervalState.Measured => "měřicí okno " + Display.Number(metadata.IntervalNs / 1_000_000d, " ms", 2),
        MeasurementIntervalState.NotApplicable => "okamžitý odečet", MeasurementIntervalState.Unavailable => "měřicí okno tohoto vzorku je nedostupné", _ => "měřicí okno zdroj neuvádí"
    };
    internal static string Detail(ProcessHistorySample row)
    {
        string text = row.Name + (row.OriginalNameLength > row.Name.Length ? " · uložený název je zkrácený ze " + row.OriginalNameLength + " znaků" : "")
            + "\nPůvodní PID: " + row.Key.Pid + "\nBoot: " + row.Key.Boot.High.ToString("X16") + row.Key.Boot.Low.ToString("X16")
            + "\nDruh generace: " + row.Key.Kind + " · hodnota " + row.Key.Generation
            + "\nPůvodní Created100ns: " + (row.Created100ns == 0 ? "nedostupné" : row.Created100ns.ToString())
            + "\nČas pozorování: " + Time(row.TimeNs, row.WallTimeUnixNs) + " · monotónní " + row.TimeNs + " ns"
            + "\nCPU: " + Display.Metric(row.Cpu, " %") + " · " + State(row.Cpu.State) + " · " + Interval(row.CpuContext)
            + "\nPracovní sada: " + row.WorkingBytes + " B · " + State(row.MemoryState) + "\nSoukromé bajty: " + row.PrivateBytes + " B · " + State(row.MemoryState)
            + "\nProcesové I/O čtení: " + Display.Metric(row.ReadIo, " B/s") + " · " + State(row.ReadIo.State)
            + "\nProcesové I/O zápis: " + Display.Metric(row.WriteIo, " B/s") + " · " + State(row.WriteIo.State) + " · " + Interval(row.IoContext)
            + (row.Late ? "\nVzorek byl při přijetí opožděný nebo označený jako zastaralý." : "");
        if (row.CpuContext is { } cpu) text += "\nUložená normalizace CPU: " + cpu.Normalization + " · dělitel " + cpu.NormalizationDivisor;
        return text;
    }
}
