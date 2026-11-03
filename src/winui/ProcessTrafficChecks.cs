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
        Require(retained.IdentityText.Contains("vlastník vydávajícího vlákna", StringComparison.Ordinal)
            && retained.Detail.Contains("Nejde o důkaz obecného žadatele I/O", StringComparison.Ordinal)
            && !retained.IdentityText.Contains("přímá", StringComparison.Ordinal),
            "The issuing thread owner was presented as the I/O requestor or a direct event identity.");

        var revoked = owner with { Attribution = ProcessTrafficAttribution.Unknown,
            IdentityKind = GenerationKind.Missing, Generation = 0, IdentityState = SampleState.Unavailable,
            Flags = ProcessTrafficFlags.Partial | ProcessTrafficFlags.IdentityConflict };
        rows.Upsert(revoked, 1000, 0, new(1, 2));
        var current = rows.Values.Single();
        Require(rows.Count == 1 && current.SelectionIdentity == retained.SelectionIdentity
            && current.Traffic == revoked && retained.Traffic == owner
            && current.IdentityText.Contains("přiřazení odvoláno", StringComparison.Ordinal)
            && !current.IdentityText.Contains("vlastník vydávajícího vlákna", StringComparison.Ordinal),
            "Revoking a disk owner retained its identity, added a duplicate row or changed a held historical row.");

        var network = owner with { SampleId = 2, Source = ProcessTrafficSource.Tcp,
            Direction = ProcessTrafficDirection.Sent, Attribution = ProcessTrafficAttribution.Correlated,
            IdentityState = SampleState.Unavailable };
        rows.Upsert(network, 1000, 0, new(1, 2));
        Require(rows.Values.Last().IdentityText.Contains("životnost nepotvrzená", StringComparison.Ordinal)
            && rows.Values.Last().Detail.Contains("není důkaz stejné životnosti", StringComparison.Ordinal),
            "Disk owner evidence incorrectly confirmed a network process lifetime.");
        App.Trace("Process traffic labels passed: issuing owner, requestor distinction, revocation upsert and unverified network identity");
    }
}
