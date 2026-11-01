using System.Buffers.Binary;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

[Flags]
internal enum ProcessTreeActionsFlags : uint { NonAtomic = 1, TargetLimit = 2, TimeLimit = 4, PassLimit = 8, CensusFailed = 16, ParentLost = 32, Residual = 64 }
internal sealed record ProcessTreeActionsNode(ProcessActionTarget Target, uint Parent, uint Depth, uint Execution);
internal sealed record ProcessTreeActionsPlan(uint Ordinal, ProcessTreeActionsFlags Flags, uint Passes, uint ResidualCount, ProcessTreeActionsNode[] Nodes);
internal sealed class ProcessTreeActionsUnknownException(string message, ProcessTreeActionsPlan? plan, Exception? inner = null) : IOException(message, inner)
{
    internal ProcessTreeActionsPlan? Plan { get; } = plan;
}

internal static class ProcessTreeActionsProtocol
{
    internal static int PayloadLength(ReadOnlySpan<byte> header, ProcessActionFrame frame, uint bytes, uint count)
    {
        bool valid = frame switch {
            ProcessActionFrame.Accepted => count == 1 && bytes == 0 && UInt(header, 44) == 0,
            ProcessActionFrame.Rejected or ProcessActionFrame.Failed => count == 0 && bytes == 0,
            ProcessActionFrame.TreePlan => count is > 0 and <= 256 && bytes == 64 + count * 64 && UInt(header, 44) == 0,
            ProcessActionFrame.TreeResults => count is > 0 and <= 256 && bytes == 64 + count * 176 && UInt(header, 44) == 0,
            _ => false
        };
        return valid ? checked((int)bytes) : throw Invalid("Neplatný rámec procesového stromu.");
    }

    internal static ProcessTreeActionsPlan Plan(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> request,
        int count, ProcessTreeActionsPlan? previous, bool final)
    {
        uint ordinal = UInt(payload, 0), flags = UInt(payload, 4), passes = UInt(payload, 56);
        if (ordinal is < 1 or > 3 || (flags & ~127u) != 0 || (flags & 1) == 0 || passes > 3
            || !payload.Slice(8, 48).SequenceEqual(request.Slice(64, 48))
            || previous is not null && (ordinal != previous.Ordinal + (final ? 0u : 1u) || count < previous.Nodes.Length || passes < previous.Passes)
            || previous is not null && ((ProcessTreeActionsFlags)flags & previous.Flags) != previous.Flags
            || previous is null && (final || ordinal != 1)) throw Invalid("Plán není pokračováním potvrzeného kořene.");
        var nodes = new ProcessTreeActionsNode[count]; var keys = new HashSet<ProcessKey>(); var executions = new HashSet<uint>();
        var rootBoot = new BootIdentifier(ULong(request, 64), ULong(request, 72));
        for (int i = 0; i < count; ++i) {
            var row = payload.Slice(64 + i * 64, 64);
            var key = new ProcessKey(new(ULong(row, 0), ULong(row, 8)), UInt(row, 16), (GenerationKind)UInt(row, 20), ULong(row, 24));
            uint parent = UInt(row, 48), depth = UInt(row, 52), execution = UInt(row, 56);
            var target = new ProcessActionTarget(key, ULong(row, 32), ULong(row, 40));
            if (key.Boot != rootBoot || key.Pid == 0 || key.Kind is not (GenerationKind.CreationTime or GenerationKind.SequenceNumber) || key.Generation == 0
                || target.Created100ns == 0 || target.Window != 0 || !keys.Add(key) || UInt(row, 60) != 0
                || execution > 256 || execution != 0 && !executions.Add(execution)
                || i == 0 && (parent != uint.MaxValue || depth != 0 || !row[..48].SequenceEqual(request.Slice(64, 48)))
                || i > 0 && (parent >= i || depth != nodes[parent].Depth + 1 || target.Created100ns < nodes[parent].Target.Created100ns || key.Pid == nodes[parent].Target.Key.Pid)
                || i > 0 && key.Kind == GenerationKind.CreationTime && key.Generation != target.Created100ns)
                throw Invalid("Plán obsahuje cíl bez doložené vazby na potvrzený kořen.");
            nodes[i] = new(target, parent, depth, execution);
            if (previous is not null && i < previous.Nodes.Length) {
                var old = previous.Nodes[i];
                if (old.Target != target || old.Parent != parent || old.Depth != depth || old.Execution != 0 && old.Execution != execution)
                    throw Invalid("Pracovník zaměnil již doložený cíl plánu.");
            }
        }
        if (final && previous!.Nodes.Length != count) throw Invalid("Výsledek přidal cíl mimo poslední potvrzený plán.");
        foreach (var node in nodes.Skip(1)) {
            var parent = nodes[node.Parent];
            if (node.Execution != 0 && parent.Execution != 0 && node.Execution >= parent.Execution)
                throw Invalid("Výsledek neodpovídá pořadí děti před rodiči.");
        }
        return new(ordinal, (ProcessTreeActionsFlags)flags, passes, UInt(payload, 60), nodes);
    }

    internal static ProcessActionsBatch Results(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> request, ProcessTreeActionsPlan previous)
    {
        int count = checked((int)UInt(header, 12));
        var plan = Plan(payload, request, count, previous, true);
        var results = new ProcessActionResult[count];
        for (int i = 0; i < count; ++i) {
            var row = payload.Slice(64 + count * 64 + i * 112, 112);
            var node = plan.Nodes[i]; var expected = payload.Slice(64 + i * 64, 64);
            var outcome = (ProcessActionOutcome)UInt(row, 48); var reason = (ProcessActionReason)UInt(row, 52); var flags = (ProcessActionFlags)UInt(row, 64);
            const ProcessActionFlags mutation = ProcessActionFlags.IdentityVerified | ProcessActionFlags.AliveBefore | ProcessActionFlags.MayHaveApplied;
            if (!row[..40].SequenceEqual(expected[..40]) || UInt(row, 108) != 0 || (uint)reason > (uint)ProcessActionReason.TreeUncertain
                || outcome is ProcessActionOutcome.Inspected or ProcessActionOutcome.CloseRequested or ProcessActionOutcome.NativeRequestAccepted
                || (uint)outcome > (uint)ProcessActionOutcome.NotPerformed || ((uint)flags & ~255u) != 0 || UInt(row, 100) != 0 || UInt(row, 104) != 0
                || flags.HasFlag(ProcessActionFlags.AliveAfter) && flags.HasFlag(ProcessActionFlags.ExitedAfter)
                || flags.HasFlag(ProcessActionFlags.IdentityVerified) && ULong(row, 40) != node.Target.Created100ns
                || flags.HasFlag(ProcessActionFlags.PriorityAvailable) && (UInt(row, 68) == 0 || UInt(row, 92) != 0)
                || !flags.HasFlag(ProcessActionFlags.PriorityAvailable) && UInt(row, 68) != 0
                || flags.HasFlag(ProcessActionFlags.AffinityAvailable) && (ULong(row, 72) == 0 || ULong(row, 80) == 0 || (ULong(row, 72) & ~ULong(row, 80)) != 0 || UInt(row, 88) > ushort.MaxValue || UInt(row, 96) != 0)
                || !flags.HasFlag(ProcessActionFlags.AffinityAvailable) && (ULong(row, 72) != 0 || ULong(row, 80) != 0 || UInt(row, 88) != 0)
                || flags.HasFlag(ProcessActionFlags.EffectConfirmed) && outcome != ProcessActionOutcome.Applied
                || outcome == ProcessActionOutcome.Applied && (flags & (mutation | ProcessActionFlags.ExitedAfter | ProcessActionFlags.EffectConfirmed)) != (mutation | ProcessActionFlags.ExitedAfter | ProcessActionFlags.EffectConfirmed)
                || outcome == ProcessActionOutcome.TerminationRequested && (flags & mutation) != mutation
                || flags.HasFlag(ProcessActionFlags.MayHaveApplied) != (node.Execution != 0)
                || outcome == ProcessActionOutcome.NotPerformed && (node.Execution != 0 || flags.HasFlag(ProcessActionFlags.MayHaveApplied)))
                throw Invalid("Neplatný nebo neověřený výsledek cíle stromu.");
            results[i] = new(node.Target, ULong(row, 40), outcome, reason, UInt(row, 56), unchecked((int)UInt(row, 60)), flags,
                UInt(row, 68), ULong(row, 72), ULong(row, 80), UInt(row, 88), UInt(row, 92), UInt(row, 96), 0, 0);
        }
        return new(ProcessActionCommand.TerminateTree, ProcessActionFrame.TreeResults, 0, results, DateTimeOffset.UtcNow) { Accepted = true, Tree = plan };
    }

    private static uint UInt(ReadOnlySpan<byte> value, int at) => BinaryPrimitives.ReadUInt32LittleEndian(value[at..]);
    private static ulong ULong(ReadOnlySpan<byte> value, int at) => BinaryPrimitives.ReadUInt64LittleEndian(value[at..]);
    private static InvalidDataException Invalid(string message) => new(message);
}
