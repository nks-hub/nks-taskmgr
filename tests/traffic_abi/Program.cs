using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NksTaskMgr.Native;

internal static partial class Program
{
    private static int cases;

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static NativeEventFrame Frame(uint variant)
    {
        var result = MakeFrame(variant, out var frame);
        if (result == NativeResult.Ok && !frame.IsInvalid) return frame;
        frame?.Dispose();
        throw new InvalidOperationException($"Traffic fixture {variant} failed: {result}.");
    }

    private static void NoRetainedFrames()
    {
        Require(Counts(out uint frames, out uint monitors, out ulong budget) == NativeResult.Ok
            && frames == 0 && monitors == 0 && budget == 0,
            "The actual C API retained frames, monitors or shared-budget allocations.");
    }

    private static void VerifiedOwnerAndRetainedData()
    {
        using (var frame = Frame(0)) {
            var batch = frame.CopyTraffic();
            Require(batch.Samples.Length == 1, "The core did not serialize exactly one traffic observation.");
            var sample = batch.Samples[0];
            Require(sample.Pid == 42 && sample.IdentityKind == GenerationKind.CreationTime && sample.Generation == 9000
                && sample.Attribution == ProcessTrafficAttribution.IssuingThreadOwner && sample.IdentityState == SampleState.Valid
                && sample.Source == ProcessTrafficSource.PhysicalDisk && sample.Direction == ProcessTrafficDirection.Read,
                "The core issuing owner changed while crossing the C ABI and managed decoder.");
            Require(sample.ObservedBytes == 4096 && sample.Operations == 1 && sample.ObservedBytesPerSecond == 4096
                && sample.State == SampleState.Gap && sample.Flags.HasFlag(ProcessTrafficFlags.Partial)
                && sample.Measurement.IntervalNs == 1_000_000_000 && sample.AttributionEpoch == batch.Info.AttributionEpoch,
                "The ABI changed counters, partial status, measurement interval or attribution epoch.");
            var start = frame.CopyEvents().Single();
            Require(start.Pid == 42 && start.Generation == 9000 && start.IdentityState == SampleState.Valid,
                "The native frame lost its source process lifetime.");
            GC.Collect(); GC.WaitForPendingFinalizers();
            Require(frame.CopyTraffic().Samples.Single() == sample && frame.CopyEvents().Single() == start,
                "A retained frame depended on the destroyed source monitor or a garbage-collected object.");
        }
        NoRetainedFrames(); ++cases;
    }

    private static void Reject(uint variant, string reason)
    {
        using (var frame = Frame(variant)) {
            bool rejected = false;
            try { frame.CopyTraffic(); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Managed ABI validation accepted " + reason + ".");
        }
        NoRetainedFrames(); ++cases;
    }

    private static void RevocationKeepsPublishedIdentity()
    {
        var result = MakePair(out var before, out var after);
        using (before) using (after) {
            Require(result == NativeResult.Ok && !before.IsInvalid && !after.IsInvalid,
                "The core did not publish both the original observation and its loss revocation.");
            var original = before.CopyTraffic().Samples.Single();
            var revokedBatch = after.CopyTraffic();
            var revoked = revokedBatch.Samples.Single();
            Require(original.Attribution == ProcessTrafficAttribution.IssuingThreadOwner
                && revoked.Attribution == ProcessTrafficAttribution.Unknown && revoked.IdentityState == SampleState.Unavailable
                && revoked.IdentityKind == GenerationKind.Missing && revoked.Generation == 0 && revoked.Pid == 0,
                "Native source loss did not remove the historical owner at the managed boundary.");
            Require(revoked.SampleId == original.SampleId && revoked.AttributionEpoch == original.AttributionEpoch
                && revokedBatch.Info.AttributionEpoch > revoked.AttributionEpoch
                && revoked.ObservedBytes == original.ObservedBytes && revoked.Operations == original.Operations
                && revoked.State == SampleState.Gap && revoked.Flags.HasFlag(ProcessTrafficFlags.Partial)
                && before.Info.ProviderEventsLost == 0 && after.Info.ProviderEventsLost == 1,
                "Loss changed an upsert key, erased observed bytes or crossed attribution epochs.");
            Require(before.CopyTraffic().Samples.Single() == original,
                "Revocation mutated a previously retained native frame.");
        }
        NoRetainedFrames(); ++cases;
    }

    private static void Verify()
    {
        Require(!RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled,
            "Traffic ABI verification requires a published Native AOT executable.");
        NoRetainedFrames();
        VerifiedOwnerAndRetainedData();
        Reject(1, "a disk owner attached to TCP");
        Reject(2, "an issuing owner without a creation time");
        Reject(3, "a sequence number instead of the historical creation time");
        Reject(4, "an unverified issuing owner");
        Reject(5, "an issuing owner claiming complete traffic");
        Reject(6, "an issuing owner without Partial");
        Reject(7, "an issuing owner without a boot identity");
        Reject(8, "an unknown attribution discriminator");
        Reject(9, "IPv6 on physical disk traffic");
        Reject(10, "unknown traffic flags");
        RevocationKeepsPublishedIdentity();
    }

    public static int Main(string[] args)
    {
        if (args is ["--wait-for-stdin"]) {
            Console.WriteLine("READY: waiting for run");
            if (Console.ReadLine() != "run") { Console.Error.WriteLine("Expected stdin line run."); return 2; }
        } else if (args.Length != 0) { Console.Error.WriteLine("Usage: TrafficAbiProbe [--wait-for-stdin]"); return 2; }
        try {
            Verify(); Console.WriteLine($"{cases} traffic ABI checks passed; live ETW capture=False"); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }

    [LibraryImport("nks-monitor.dll", EntryPoint = "nks_test_traffic_frame")]
    private static partial NativeResult MakeFrame(uint variant, out NativeEventFrame frame);
    [LibraryImport("nks-monitor.dll", EntryPoint = "nks_test_traffic_pair")]
    private static partial NativeResult MakePair(out NativeEventFrame before, out NativeEventFrame after);
    [LibraryImport("nks-monitor.dll", EntryPoint = "nks_test_traffic_counts")]
    private static partial NativeResult Counts(out uint frames, out uint monitors, out ulong budget);
}
