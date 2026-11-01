using System.Buffers.Binary;

namespace NksTaskMgr.WinUI;

internal sealed record ProcessCpuSet(uint Id, uint Group, uint LogicalProcessor, uint Core, uint Numa, uint LastLevelCache,
    uint Efficiency, uint Flags)
{
    internal bool Available => (Flags & 2) == 0 || (Flags & 4) != 0;
}
internal sealed record ProcessCpuSetsSnapshot(bool Valid, string Baseline, IReadOnlyList<ProcessCpuSet> Processors, IReadOnlyList<uint> DefaultIds);
internal sealed record ProcessCpuSetsSelection(string Baseline, IReadOnlyList<uint> Ids);

internal static partial class ProcessActionsProtocol
{
    private const int MaximumCpuSets = 4096;
    private const int MaximumCpuSetsResponse = 112 + 48 + MaximumCpuSets * 36;

    private static byte[] CpuSetsRequest(ProcessActionsRequest request)
    {
        bool query = request.Command == ProcessActionCommand.QueryCpuSets;
        if (request.Targets.Length != 1 || request.Argument != 0 || request.Group != 0
            || (query ? request.CpuSets is not null : request.CpuSets is null))
            throw new ArgumentException("CPU sets vyžadují jeden proces a ověřený náhled.", nameof(request));
        var basis = Request(request with { Command = ProcessActionCommand.Query, CpuSets = null });
        if (query) {
            BinaryPrimitives.WriteUInt16LittleEndian(basis.AsSpan(6), (ushort)request.Command);
            return basis;
        }
        var selection = request.CpuSets!;
        var ids = selection.Ids.ToArray();
        if (ids.Length > MaximumCpuSets || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("Výběr CPU sets je příliš velký nebo obsahuje opakované ID.", nameof(request));
        Array.Sort(ids);
        byte[] baseline;
        try { baseline = Convert.FromHexString(selection.Baseline); }
        catch (FormatException error) { throw new ArgumentException("Neplatný otisk náhledu CPU sets.", nameof(request), error); }
        if (baseline.Length != 32 || !baseline.Any(value => value != 0))
            throw new ArgumentException("Chybí ověřený otisk náhledu CPU sets.", nameof(request));
        var bytes = new byte[112 + 32 + ids.Length * 4];
        basis.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)request.Command);
        Write(bytes, 8, (uint)(bytes.Length - 64)); Write(bytes, 40, (ulong)ids.Length);
        baseline.CopyTo(bytes, 112);
        for (int i = 0; i < ids.Length; ++i) Write(bytes, 144 + i * 4, ids[i]);
        return bytes;
    }

    private static int CpuSetsPayloadLength(ReadOnlySpan<byte> header, ProcessActionFrame frame, uint bytes, uint count)
    {
        bool valid = frame switch {
            ProcessActionFrame.Accepted => count == 1 && bytes == 0 && UInt(header, 44) == 0,
            ProcessActionFrame.CpuSets => count == 1 && bytes is >= 160 and <= MaximumCpuSetsResponse && UInt(header, 44) == 0,
            ProcessActionFrame.Rejected or ProcessActionFrame.Failed => count == 0 && bytes == 0,
            _ => false
        };
        if (!valid) throw Invalid("Pracovník vrátil neplatný typ nebo délku odpovědi CPU sets.");
        return checked((int)bytes);
    }

    private static ProcessCpuSetsSnapshot CpuSetsSnapshot(ReadOnlySpan<byte> payload, ProcessActionResult result, ReadOnlySpan<byte> request)
    {
        if (payload.Length < 48) throw Invalid("Chybí úplná odpověď CPU sets.");
        uint count = UInt(payload, 32), defaults = UInt(payload, 36), flags = UInt(payload, 40);
        if (flags > 1 || UInt(payload, 44) != 0 || count > MaximumCpuSets || defaults > count
            || payload.Length != 48 + count * 32 + defaults * 4)
            throw Invalid("Neplatné délky nebo příznaky CPU sets.");
        bool hasBaseline = false;
        foreach (byte value in payload[..32]) hasBaseline |= value != 0;
        if (flags == 0) {
            if (count != 0 || defaults != 0 || hasBaseline || result.Outcome is ProcessActionOutcome.Inspected or ProcessActionOutcome.Applied)
                throw Invalid("Neplatný stav nedostupných CPU sets.");
            return new(false, "", Array.AsReadOnly(Array.Empty<ProcessCpuSet>()), Array.AsReadOnly(Array.Empty<uint>()));
        }
        if (count == 0 || !hasBaseline || !result.Flags.HasFlag(ProcessActionFlags.IdentityVerified))
            throw Invalid("CPU sets nejsou vázané na ověřený proces.");
        var rows = new ProcessCpuSet[count];
        var ids = new HashSet<uint>();
        for (int i = 0; i < rows.Length; ++i) {
            var row = payload.Slice(48 + i * 32, 32);
            var cpu = new ProcessCpuSet(UInt(row, 0), UInt(row, 4), UInt(row, 8), UInt(row, 12), UInt(row, 16), UInt(row, 20), UInt(row, 24), UInt(row, 28));
            if (!ids.Add(cpu.Id) || cpu.Group > ushort.MaxValue || cpu.LogicalProcessor >= 64
                || cpu.Core > byte.MaxValue || cpu.Numa > byte.MaxValue || cpu.LastLevelCache > byte.MaxValue || cpu.Efficiency > byte.MaxValue || (cpu.Flags & ~15u) != 0)
                throw Invalid("Neplatný nebo opakovaný logický procesor v CPU sets.");
            rows[i] = cpu;
        }
        var selected = new uint[defaults];
        int start = checked(48 + rows.Length * 32);
        for (int i = 0; i < selected.Length; ++i) {
            uint id = UInt(payload, start + i * 4);
            if (!ids.Contains(id) || i > 0 && id <= selected[i - 1]) throw Invalid("Neplatný výchozí výběr CPU sets.");
            selected[i] = id;
        }
        if (result.Outcome == ProcessActionOutcome.Applied) {
            if (!result.Flags.HasFlag(ProcessActionFlags.AliveAfter)) throw Invalid("Proces po změně CPU sets nemá potvrzenou životnost.");
            uint expected = checked((uint)ULong(request, 40));
            if (defaults != expected || request.Length != 144 + expected * 4) throw Invalid("Zpětné čtení CPU sets nepotvrdilo počet vybraných procesorů.");
            for (int i = 0; i < selected.Length; ++i)
                if (selected[i] != UInt(request, 144 + i * 4)) throw Invalid("Zpětné čtení CPU sets nepotvrdilo požadovaný výběr.");
        }
        return new(true, Convert.ToHexString(payload[..32]), Array.AsReadOnly(rows), Array.AsReadOnly(selected));
    }
}
