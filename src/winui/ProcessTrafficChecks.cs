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
        var rows = new ProcessTrafficRows();
        rows.Upsert(owner, 1000, 0, new(1, 2));
        var retained = rows.Values.Single();
        string ownerText = Loc.T("Pozorovaná podmnožina · vlastník vydávajícího vlákna při zahájení");
        Require(retained.IdentityText.Contains(ownerText, StringComparison.Ordinal)
            && retained.Detail.Contains(Loc.T("INIT a dokončení téhož I/O doložily životnost vlastníka vydávajícího vlákna při zahájení. Nejde o důkaz obecného žadatele I/O; vlákno může být připojené k jinému procesu."), StringComparison.Ordinal)
            && !retained.IdentityText.Contains(Loc.T("Pozorovaná podmnožina · PID z události, generace z nepřerušené evidence"), StringComparison.Ordinal),
            "The issuing thread owner was presented as the I/O requestor or a direct event identity.");

        var revoked = owner with { Attribution = ProcessTrafficAttribution.Unknown,
            IdentityKind = GenerationKind.Missing, Generation = 0, IdentityState = SampleState.Unavailable,
            Flags = ProcessTrafficFlags.Partial | ProcessTrafficFlags.IdentityConflict };
        rows.Upsert(revoked, 1000, 0, new(1, 2));
        var current = rows.Values.Single();
        Require(rows.Count == 1 && current.SelectionIdentity == retained.SelectionIdentity
            && current.Traffic == revoked && retained.Traffic == owner
            && current.IdentityText.Contains(Loc.T("dřívější přiřazení odvoláno"), StringComparison.Ordinal)
            && !current.IdentityText.Contains(ownerText, StringComparison.Ordinal),
            "Revoking a disk owner retained its identity, added a duplicate row or changed a held historical row.");

        var network = owner with { SampleId = 2, Source = ProcessTrafficSource.Tcp,
            Direction = ProcessTrafficDirection.Sent, Attribution = ProcessTrafficAttribution.Correlated,
            IdentityState = SampleState.Unavailable };
        rows.Upsert(network, 1000, 0, new(1, 2));
        Require(rows.Values.Last().IdentityText.Contains(Loc.T("Částečné · korelace PID/času · životnost nepotvrzená"), StringComparison.Ordinal)
            && rows.Values.Last().Detail.Contains(Loc.T("Kandidát podle historického PID a času; není důkaz stejné životnosti."), StringComparison.Ordinal),
            "Disk owner evidence incorrectly confirmed a network process lifetime.");
        App.Trace("Process traffic labels passed: issuing owner, requestor distinction, revocation upsert and unverified network identity");
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
        ProcessNetwork.Observe(running with { State = ProcessEventMonitorState.Stopped }, null);
        row.SetNetwork(null, false, false, false);
        Require(ProcessNetwork.Rates(out _, out _, later) is null && row.NetworkText == "—", "Network rates survived the end of the ETW capture.");
        App.Trace("Process network rates passed: per-generation window, revocation, partial window and capture end");
    }
}
