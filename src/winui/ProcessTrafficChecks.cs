using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static void ProcessTrafficAttributionLabels()
    {
        var metadata = new MeasurementMetadata {
            Unit = MeasurementUnit.BytesPerSecond, Normalization = MeasurementNormalization.None,
            NormalizationDivisor = 1, Basis = MeasurementBasis.ElapsedDelta,
            IntervalState = MeasurementIntervalState.Measured, IntervalNs = 1_000_000_000, ObservedAtNs = 10_000_000_000
        };
        var owner = new ProcessTrafficSample(1, 1000, 2000, metadata.ObservedAtNs, 100,
            GenerationKind.CreationTime, 500, SampleState.Valid, ProcessTrafficAttribution.IssuingThreadOwner,
            ProcessTrafficSource.PhysicalDisk, ProcessTrafficDirection.Read, 4096, 1, 4096,
            SampleState.Gap, ProcessTrafficFlags.Partial, 1, metadata);
        ProcessNetworkRates(owner);
    }

    private static void ProcessNetworkRates(ProcessTrafficSample template)
    {
        long frequency = System.Diagnostics.Stopwatch.Frequency, later = System.Diagnostics.Stopwatch.GetTimestamp() + 20 * frequency;
        long window = (later / frequency - 5) * frequency;
        var running = new ProcessEventBatchInfo(ProcessEventMonitorState.Running, 0, 0, new(1, 2), SampleState.Valid, frequency, 0, 0, 0, 0, 0, 0);
        var info = new ProcessTrafficBatchInfo(SampleState.Gap, SampleState.Gap, SampleState.Valid, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 8 * 1024 * 1024);
        var sent = template with { SampleId = 10, StartQpc = window, EndQpc = window + frequency, Source = ProcessTrafficSource.Tcp,
            Direction = ProcessTrafficDirection.Sent, Attribution = ProcessTrafficAttribution.Direct, ObservedBytes = 1000 };
        var received = sent with { SampleId = 11, Source = ProcessTrafficSource.Udp, Direction = ProcessTrafficDirection.Received, ObservedBytes = 500 };
        var older = sent with { SampleId = 12, StartQpc = window - frequency, EndQpc = window, ObservedBytes = 2000 };
        var key = new ProcessKey(new(1, 2), template.Pid, GenerationKind.CreationTime, template.Generation);
        ProcessNetwork.Clear();
        ProcessNetwork.Observe(running, new(info, [sent, received, older, template]));
        var rates = ProcessNetwork.Rates(out bool partial, out bool totalPartial, later);
        Require(rates is not null && rates[key] == new ProcessNetworkRate(1000, 500, 3500) && !partial && !totalPartial
            && ProcessNetwork.Rates(out _, out _) is null, "Direct network samples did not sum per generation or an incomplete window was shown.");
        ProcessNetwork.Observe(running, new(info, [sent with { Attribution = ProcessTrafficAttribution.Unknown, IdentityKind = GenerationKind.Missing,
            Generation = 0, Pid = 0, IdentityState = SampleState.Unavailable }]));
        rates = ProcessNetwork.Rates(out partial, out totalPartial, later);
        var row = new ProcessRow(key);
        row.SetNetwork(rates![key], partial, totalPartial, false);
        Require(rates[key] == new ProcessNetworkRate(0, 500, 2500) && partial && totalPartial && row.NetworkText.StartsWith("≥ ", StringComparison.Ordinal)
            && row.TableMetricsDescription.Contains("≥ " + (2500 / 1024d).ToString("N1", Loc.Culture) + " KiB", StringComparison.Ordinal), "A revoked network sample stayed in the sum or the partial window was not marked.");
        string split = Loc.F("Síť: příjem {0} · odeslání {1}", "≥ " + ProcessNetworkRate.Text(500), "≥ " + ProcessNetworkRate.Text(0));
        Require(row.NetworkRate == rates[key] && row.TableMetricsDescription.Contains(split, StringComparison.Ordinal)
            && MainWindow.NetworkTip([row]).Contains(split, StringComparison.Ordinal), "Received and sent traffic was not kept apart in the row or its tooltips.");
        ProcessNetwork.Observe(running with { State = ProcessEventMonitorState.Stopped }, null);
        row.SetNetwork(null, false, false, false);
        Require(ProcessNetwork.Rates(out _, out _, later) is null && row.NetworkText == "—" && row.NetworkRate is null && MainWindow.NetworkTip([row]).Length == 0,
            "Network rates survived the end of the ETW capture.");
        App.Trace("Process network rates passed: per-generation window, revocation, partial window, received/sent split and capture end");
    }
}
