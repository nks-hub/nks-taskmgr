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
        if (metadata is not { Unit: not MeasurementUnit.Unknown } item) return "kontext měření nedostupný";
        return item.IntervalState switch {
            MeasurementIntervalState.Measured => "měřicí okno " + Display.Number(item.IntervalNs / 1_000_000d, " ms", 1),
            MeasurementIntervalState.NotApplicable => "okamžitý odečet", MeasurementIntervalState.Unavailable => "měřicí okno zatím nedostupné",
            _ => "měřicí okno zdroj neuvádí"
        };
    }

    internal static string Describe(MeasurementMetadata? metadata, Metric metric, bool stale)
    {
        string state = stale ? "Zastaralá data" : metric.State == SampleState.Valid ? "Platný vzorek" : Display.Metric(metric, "");
        string text = "Stav: " + state + "\nZdroj: " + Source(metric.Source);
        return text + "\n" + DescribeFormat(metadata);
    }

    internal static string DescribeFormat(MeasurementMetadata? metadata)
    {
        if (metadata is not { Unit: not MeasurementUnit.Unknown } item) return "Kontext měření není dostupný.";
        string text = "Jednotka vzorku: " + Unit(item.Unit) + "\nVýpočet: " + Basis(item.Basis);
        text += "\nNormalizace: " + (item.Normalization switch {
            MeasurementNormalization.WholeMachine => "100 % znamená celý počítač", MeasurementNormalization.LogicalProcessor => "100 % znamená jeden logický procesor",
            MeasurementNormalization.BusiestGpuEngine => "nejvytíženější GPU engine", MeasurementNormalization.DeviceActiveTime => "aktivní čas zařízení", _ => "bez normalizace"
        });
        if (item.NormalizationDivisor > 1) text += "; použitý dělitel " + item.NormalizationDivisor;
        else if (item.NormalizationDivisor == 0) text += "; dělitel není známý";
        text += "\n" + Summary(item);
        text += "\nOdstup pozorování: " + (item.CadenceNs > 0 ? Display.Number(item.CadenceNs / 1_000_000d, " ms", 1) : "zatím nedostupný");
        if (item.ObservedAtNs > 0) text += "\nPozorováno před " + Display.Number(Math.Max(0, NativeMonitor.MonotonicNowNs - item.ObservedAtNs) / 1_000_000_000d, " s", 2);
        return text;
    }

    internal static string Source(MetricSource value) => value switch {
        MetricSource.NativeProcess => "nativní statistiky procesů", MetricSource.ProcessorTime => "časové čítače procesorů",
        MetricSource.PerformanceInfo => "systémové paměťové statistiky", MetricSource.PerformanceCounter => "čítač výkonu Windows (PDH)",
        MetricSource.NetworkInterface => "čítače síťového adaptéru", MetricSource.Dxgi => "DXGI", MetricSource.GpuAdapter => "ovladač grafického adaptéru", _ => "neznámý"
    };
    private static string Unit(MeasurementUnit value) => value switch {
        MeasurementUnit.Percent => "%", MeasurementUnit.Bytes => "B", MeasurementUnit.BytesPerSecond => "B/s", MeasurementUnit.BitsPerSecond => "bit/s",
        MeasurementUnit.Celsius => "°C", MeasurementUnit.Seconds => "s", MeasurementUnit.Count => "počet", MeasurementUnit.OperationsPerSecond => "operace/s", _ => "neznámá"
    };
    private static string Basis(MeasurementBasis value) => value switch {
        MeasurementBasis.Gauge => "okamžitá hodnota", MeasurementBasis.ElapsedDelta => "rozdíl čítačů za skutečný časový interval",
        MeasurementBasis.CounterRatio => "poměr rozdílů čítačů", MeasurementBasis.ProviderDefined => "určuje poskytovatel", _ => "neznámý"
    };
}
