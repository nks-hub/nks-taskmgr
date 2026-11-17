using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace NksTaskMgr.WinUI;

/// <summary>
/// Which processes own TCP connections or UDP endpoints on the local addresses of which network interface, from the
/// IP Helper tables that a standard user may read. A process with sockets on several interfaces belongs to each of
/// them. Sockets bound to every address (0.0.0.0, ::) name no interface; a process that has only those is listed under
/// the interface of the default route.
/// </summary>
internal static partial class ProcessSockets
{
    private const int AfInet = 2, AfInet6 = 23, TcpOwnerPidAll = 5, UdpOwnerPid = 1, UnicastRow = 80;
    private static readonly Dictionary<ulong, HashSet<uint>> owners = [];
    private static readonly Dictionary<uint, int> interfaces = [];
    private static long readAt = long.MinValue;
    private static bool valid;

    /// <summary>PIDs with a socket on the interface; null when the tables cannot be read.</summary>
    internal static IReadOnlySet<uint>? Owners(ulong interfaceLuid)
    {
        Refresh();
        return !valid ? null : owners.TryGetValue(interfaceLuid, out var pids) ? pids : EmptyPids;
    }

    /// <summary>True when the process has sockets on more than one interface, so its rate is an upper bound for each.</summary>
    internal static bool Shared(uint pid) => interfaces.TryGetValue(pid, out int count) && count > 1;

    private static readonly HashSet<uint> EmptyPids = [];

    private static void Refresh()
    {
        long now = Environment.TickCount64;
        if (now - readAt is >= 0 and < 2000) return;
        readAt = now;
        owners.Clear(); interfaces.Clear();
        valid = Read(out var addresses);
        if (!valid) return;
        var seen = new Dictionary<uint, HashSet<ulong>>();
        var unbound = new HashSet<uint>();
        void Add(uint pid, Address address)
        {
            if (pid == 0) return;
            if (address.Value == 0) { unbound.Add(pid); return; }
            if (!addresses.TryGetValue(address, out ulong luid)) return;
            if (!owners.TryGetValue(luid, out var pids)) owners.Add(luid, pids = []);
            pids.Add(pid);
            if (address.Loopback) return;
            if (!seen.TryGetValue(pid, out var luids)) seen.Add(pid, luids = []);
            luids.Add(luid);
        }
        valid = Table(true, AfInet, 24, row => Add(BinaryPrimitives.ReadUInt32LittleEndian(row[20..]), Address.V4(row[4..8])))
            && Table(true, AfInet6, 56, row => Add(BinaryPrimitives.ReadUInt32LittleEndian(row[52..]), Address.V6(row[..16], BinaryPrimitives.ReadUInt32LittleEndian(row[16..]))))
            && Table(false, AfInet, 12, row => Add(BinaryPrimitives.ReadUInt32LittleEndian(row[8..]), Address.V4(row[..4])))
            && Table(false, AfInet6, 28, row => Add(BinaryPrimitives.ReadUInt32LittleEndian(row[24..]), Address.V6(row[..16], BinaryPrimitives.ReadUInt32LittleEndian(row[16..]))));
        foreach (var (pid, luids) in seen) interfaces[pid] = luids.Count;
        // A socket bound to every address sends through the route of its destination; with no other evidence the
        // process is listed under the interface of the default route.
        if (DefaultInterface() is not { } route) return;
        if (!owners.TryGetValue(route, out var routed)) owners.Add(route, routed = []);
        foreach (uint pid in unbound) if (!seen.ContainsKey(pid)) routed.Add(pid);
    }

    /// <summary>The interface that carries traffic to a public IPv4 address; null without such a route.</summary>
    internal static ulong? DefaultInterface() =>
        // 192.0.2.1 (TEST-NET-1) in network byte order: no host route can exist for it.
        GetBestInterface(0x010200C0, out uint index) == 0 && ConvertInterfaceIndexToLuid(index, out ulong luid) == 0 ? luid : null;

    /// <summary>A local address; link-local IPv6 repeats on every interface, so its zone is part of the identity.</summary>
    internal readonly record struct Address(UInt128 Value, uint Zone, bool Ipv6)
    {
        internal static Address V4(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt32BigEndian(bytes), 0, false);
        internal static Address V6(ReadOnlySpan<byte> bytes, uint zone) => new(BinaryPrimitives.ReadUInt128BigEndian(bytes), bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80 ? zone : 0, true);
        internal bool Loopback => Ipv6 ? Value == 1 : Value >> 24 == 127;
    }

    private delegate void RowReader(ReadOnlySpan<byte> row);

    private static unsafe bool Read(out Dictionary<Address, ulong> addresses)
    {
        addresses = [];
        if (GetUnicastIpAddressTable(0, out nint table) != 0) return false;
        try {
            uint count = *(uint*)table;
            var rows = new ReadOnlySpan<byte>((byte*)table + 8, checked((int)count * UnicastRow));
            for (int i = 0; i < count; ++i) {
                var row = rows.Slice(i * UnicastRow, UnicastRow);
                ushort family = BinaryPrimitives.ReadUInt16LittleEndian(row);
                ulong luid = BinaryPrimitives.ReadUInt64LittleEndian(row[32..]);
                if (family == AfInet) addresses[Address.V4(row[4..8])] = luid;
                else if (family == AfInet6) {
                    uint zone = BinaryPrimitives.ReadUInt32LittleEndian(row[24..]);
                    addresses[Address.V6(row[8..24], zone != 0 ? zone : BinaryPrimitives.ReadUInt32LittleEndian(row[68..]))] = luid;
                }
            }
            return true;
        } finally { FreeMibTable(table); }
    }

    private static bool Table(bool tcp, int family, int rowSize, RowReader read)
    {
        int size = 0;
        for (int attempt = 0; attempt < 4; ++attempt) {
            var buffer = new byte[size];
            uint status = tcp ? GetExtendedTcpTable(buffer, ref size, false, family, TcpOwnerPidAll, 0) : GetExtendedUdpTable(buffer, ref size, false, family, UdpOwnerPid, 0);
            if (status == 122) continue;
            if (status != 0 || buffer.Length < 4) return status == 0;
            int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(buffer), (uint)((buffer.Length - 4) / rowSize));
            for (int i = 0; i < count; ++i) read(buffer.AsSpan(4 + i * rowSize, rowSize));
            return true;
        }
        return false;
    }

    internal static void Check()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        Require(Address.V4([127, 0, 0, 1]).Loopback && !Address.V4([192, 0, 2, 1]).Loopback && Address.V4([192, 0, 2, 1]) == Address.V4([192, 0, 2, 1]), "IPv4 addresses were compared wrong.");
        byte[] linkLocal = [0xfe, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1], global = [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1];
        Require(Address.V6(linkLocal, 4) != Address.V6(linkLocal, 7) && Address.V6(global, 4) == Address.V6(global, 7), "A link-local address lost its zone or a global address kept one.");
        byte[] loopback = new byte[16]; loopback[15] = 1;
        Require(Address.V6(loopback, 0).Loopback && !Address.V6(global, 0).Loopback, "The IPv6 loopback address was not recognized.");
        // A listening TCP socket on the loopback address must put this process on the loopback interface.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        readAt = long.MinValue;
        ulong luid = 0;
        Require(Read(out var addresses) && addresses.TryGetValue(Address.V4([127, 0, 0, 1]), out luid), "The loopback address has no interface in the unicast table.");
        Require(Owners(luid) is { } pids && pids.Contains((uint)Environment.ProcessId), "A listening socket did not assign its process to the interface.");
        Require(Owners(ulong.MaxValue) is { Count: 0 }, "An unknown interface has socket owners.");
        // This process has no socket on a specific non-loopback address, so its unbound UDP socket follows the default route.
        using var unbound = new System.Net.Sockets.UdpClient(0);
        readAt = long.MinValue;
        Require(DefaultInterface() is not { } route || Owners(route) is { } routed && (routed.Contains((uint)Environment.ProcessId) || interfaces.ContainsKey((uint)Environment.ProcessId)),
            "A socket bound to every address did not assign its process to the interface of the default route.");
        App.Trace("Process sockets passed: address identity, link-local zones, loopback, socket owners by interface and unbound sockets on the default route");
    }

    [LibraryImport("iphlpapi.dll")] private static partial uint GetUnicastIpAddressTable(ushort family, out nint table);
    [LibraryImport("iphlpapi.dll")] private static partial void FreeMibTable(nint table);
    [LibraryImport("iphlpapi.dll")] private static partial uint GetBestInterface(uint destination, out uint index);
    [LibraryImport("iphlpapi.dll")] private static partial uint ConvertInterfaceIndexToLuid(uint index, out ulong luid);
    [LibraryImport("iphlpapi.dll")] private static partial uint GetExtendedTcpTable(byte[] table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    [LibraryImport("iphlpapi.dll")] private static partial uint GetExtendedUdpTable(byte[] table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
