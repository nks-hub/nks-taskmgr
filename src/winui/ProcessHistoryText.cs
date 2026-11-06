using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static class ProcessHistoryText
{
    internal static string Time(long time, long wall)
        => wall > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(wall / 1_000_000).LocalDateTime.ToString("HH:mm:ss.fff") : Mono(time);
    internal static string Mono(long value) => value > 0 ? Display.Number(value / 1_000_000_000d, " s", 3) : Loc.T("nedostupný");
    internal static string State(SampleState value) => value switch {
        SampleState.Valid => Loc.T("platný"), SampleState.First => Loc.T("první vzorek"), SampleState.Unavailable => Loc.T("nedostupné"), SampleState.AccessDenied => Loc.T("přístup odmítnut"),
        SampleState.Unsupported => Loc.T("nepodporované"), SampleState.Stale => Loc.T("zastaralé"), SampleState.Gap => Loc.T("mezera"), _ => Loc.T("chyba")
    };
    private static string Interval(MeasurementMetadata? value) => value is not { } metadata ? Loc.T("měřicí okno není uložené") : metadata.IntervalState switch {
        MeasurementIntervalState.Measured => Loc.F("měřicí okno {0}", Display.Number(metadata.IntervalNs / 1_000_000d, " ms", 2)),
        MeasurementIntervalState.NotApplicable => Loc.T("okamžitý odečet"), MeasurementIntervalState.Unavailable => Loc.T("měřicí okno tohoto vzorku je nedostupné"), _ => Loc.T("měřicí okno zdroj neuvádí")
    };
    internal static string Detail(ProcessHistorySample row)
    {
        string text = row.Name + (row.OriginalNameLength > row.Name.Length ? " · " + Loc.F("uložený název je zkrácený ze {0} znaků", row.OriginalNameLength) : "")
            + "\n" + Loc.F("Původní PID: {0}", row.Key.Pid) + "\nBoot: " + row.Key.Boot.High.ToString("X16") + row.Key.Boot.Low.ToString("X16")
            + "\n" + Loc.F("Druh generace: {0} · hodnota {1}", row.Key.Kind, row.Key.Generation)
            + "\n" + Loc.F("Původní Created100ns: {0}", row.Created100ns == 0 ? Loc.T("nedostupné") : row.Created100ns.ToString())
            + "\n" + Loc.F("Čas pozorování: {0} · monotónní {1} ns", Time(row.TimeNs, row.WallTimeUnixNs), row.TimeNs)
            + "\nCPU: " + Display.Metric(row.Cpu, " %") + " · " + State(row.Cpu.State) + " · " + Interval(row.CpuContext)
            + "\n" + Loc.F("Pracovní sada: {0} B · {1}", row.WorkingBytes, State(row.MemoryState)) + "\n" + Loc.F("Soukromé bajty: {0} B · {1}", row.PrivateBytes, State(row.MemoryState))
            + "\n" + Loc.F("Procesové I/O čtení: {0} · {1}", Display.Metric(row.ReadIo, " B/s"), State(row.ReadIo.State))
            + "\n" + Loc.F("Procesové I/O zápis: {0} · {1} · {2}", Display.Metric(row.WriteIo, " B/s"), State(row.WriteIo.State), Interval(row.IoContext))
            + (row.Late ? "\n" + Loc.T("Vzorek byl při přijetí opožděný nebo označený jako zastaralý.") : "");
        if (row.CpuContext is { } cpu) text += "\n" + Loc.F("Uložená normalizace CPU: {0} · dělitel {1}", cpu.Normalization, cpu.NormalizationDivisor);
        return text;
    }
}
