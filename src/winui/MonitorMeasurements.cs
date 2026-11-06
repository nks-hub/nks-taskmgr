using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal readonly record struct ResourceMeasurementContext(MeasurementMetadata Primary, MeasurementMetadata Secondary, MeasurementMetadata Tertiary,
    MeasurementMetadata Temperature, MeasurementMetadata Capacity, MeasurementMetadata SharedCapacity, MeasurementMetadata EngineUtilization);

public sealed partial class MonitorModel
{
    private readonly Dictionary<string, ResourceMeasurementContext> resourceMeasurements = new(StringComparer.Ordinal);
    private ProcessMeasurements processMeasurements;
    private bool measurementApiMissing, measurementsAvailable;

    internal unsafe void ReadMeasurements(NativeFrame frame)
    {
        if (measurementApiMissing) return;
        try {
            frame.ReadMeasurements(data => {
                var resources = new Dictionary<string, ResourceMeasurementContext>(StringComparer.Ordinal);
                for (uint i = 0; i < data.ResourceCount; ++i) {
                    var item = data.Resources[i];
                    if (!resources.TryAdd(item.ResourceId.Copy(), new(item.Primary, item.Secondary, item.Tertiary, item.Temperature,
                        item.Capacity, item.SharedCapacity, item.EngineUtilization))) throw new NativeMonitorException(NativeResult.AbiMismatch);
                }
                if ((data.Flags & FrameFlags.ProcessData) != 0) processMeasurements = data.Processes;
                if ((data.Flags & FrameFlags.ResourceData) != 0) {
                    resourceMeasurements.Clear();
                    foreach (var pair in resources) resourceMeasurements.Add(pair.Key, pair.Value);
                }
                measurementsAvailable = true;
            });
        } catch (EntryPointNotFoundException) {
            measurementApiMissing = true; measurementsAvailable = false;
        } catch (NativeMonitorException) {
            measurementsAvailable = false; processMeasurements = default; resourceMeasurements.Clear();
        }
    }

    internal MeasurementMetadata? MeasurementFor(ResourceRow resource, bool logicalProcessors)
    {
        if (!measurementsAvailable) return null;
        if (resource.Kind == ResourceKind.Cpu) return logicalProcessors ? processMeasurements.LogicalCpu : processMeasurements.SystemCpu;
        if (resource.Kind == ResourceKind.Memory) return processMeasurements.SystemMemory;
        return resourceMeasurements.TryGetValue(resource.Id, out var value) ? value.Primary : null;
    }
}

internal static class MeasurementText
{
    internal static string Summary(MeasurementMetadata? metadata)
    {
        if (metadata is not { Unit: not MeasurementUnit.Unknown } item) return Loc.T("kontext měření nedostupný");
        return item.IntervalState switch {
            MeasurementIntervalState.Measured => Loc.F("měřicí okno {0}", Display.Number(item.IntervalNs / 1_000_000d, " ms", 1)),
            MeasurementIntervalState.NotApplicable => Loc.T("okamžitý odečet"), MeasurementIntervalState.Unavailable => Loc.T("měřicí okno zatím nedostupné"),
            _ => Loc.T("měřicí okno zdroj neuvádí")
        };
    }

    internal static string Describe(MeasurementMetadata? metadata, Metric metric, bool stale)
    {
        string state = stale ? Loc.T("Zastaralá data") : metric.State == SampleState.Valid ? Loc.T("Platný vzorek") : Display.Metric(metric, "");
        string text = Loc.F("Stav: {0}\nZdroj: {1}", state, Source(metric.Source));
        return text + "\n" + DescribeFormat(metadata);
    }

    internal static string DescribeFormat(MeasurementMetadata? metadata)
    {
        if (metadata is not { Unit: not MeasurementUnit.Unknown } item) return Loc.T("Kontext měření není dostupný.");
        string text = Loc.F("Jednotka vzorku: {0}\nVýpočet: {1}", Unit(item.Unit), Basis(item.Basis));
        text += "\n" + Loc.F("Normalizace: {0}", item.Normalization switch {
            MeasurementNormalization.WholeMachine => Loc.T("100 % znamená celý počítač"), MeasurementNormalization.LogicalProcessor => Loc.T("100 % znamená jeden logický procesor"),
            MeasurementNormalization.BusiestGpuEngine => Loc.T("nejvytíženější GPU engine"), MeasurementNormalization.DeviceActiveTime => Loc.T("aktivní čas zařízení"), _ => Loc.T("bez normalizace")
        });
        if (item.NormalizationDivisor > 1) text += "; " + Loc.F("použitý dělitel {0}", item.NormalizationDivisor);
        else if (item.NormalizationDivisor == 0) text += "; " + Loc.T("dělitel není známý");
        text += "\n" + Summary(item);
        text += "\n" + Loc.F("Odstup pozorování: {0}", item.CadenceNs > 0 ? Display.Number(item.CadenceNs / 1_000_000d, " ms", 1) : Loc.T("zatím nedostupný"));
        if (item.ObservedAtNs > 0) text += "\n" + Loc.F("Pozorováno před {0}", Display.Number(Math.Max(0, NativeMonitor.MonotonicNowNs - item.ObservedAtNs) / 1_000_000_000d, " s", 2));
        return text;
    }

    internal static string Source(MetricSource value) => value switch {
        MetricSource.NativeProcess => Loc.T("nativní statistiky procesů"), MetricSource.ProcessorTime => Loc.T("časové čítače procesorů"),
        MetricSource.PerformanceInfo => Loc.T("systémové paměťové statistiky"), MetricSource.PerformanceCounter => Loc.T("čítač výkonu Windows (PDH)"),
        MetricSource.NetworkInterface => Loc.T("čítače síťového adaptéru"), MetricSource.Dxgi => "DXGI", MetricSource.GpuAdapter => Loc.T("ovladač grafického adaptéru"), _ => Loc.T("neznámý")
    };
    private static string Unit(MeasurementUnit value) => value switch {
        MeasurementUnit.Percent => "%", MeasurementUnit.Bytes => "B", MeasurementUnit.BytesPerSecond => "B/s", MeasurementUnit.BitsPerSecond => "bit/s",
        MeasurementUnit.Celsius => "°C", MeasurementUnit.Seconds => "s", MeasurementUnit.Count => Loc.T("počet"), MeasurementUnit.OperationsPerSecond => Loc.T("operace/s"), _ => Loc.T("neznámá")
    };
    private static string Basis(MeasurementBasis value) => value switch {
        MeasurementBasis.Gauge => Loc.T("okamžitá hodnota"), MeasurementBasis.ElapsedDelta => Loc.T("rozdíl čítačů za skutečný časový interval"),
        MeasurementBasis.CounterRatio => Loc.T("poměr rozdílů čítačů"), MeasurementBasis.ProviderDefined => Loc.T("určuje poskytovatel"), _ => Loc.T("neznámý")
    };
}
