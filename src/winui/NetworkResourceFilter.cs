using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal readonly record struct NetworkFilterOptions(bool ShowDisconnected, bool ShowAuxiliary);

internal static class NetworkResourceFilter
{
    internal static bool Includes(ResourceRow row, NetworkFilterOptions options) => row.Kind != ResourceKind.Network
        || (options.ShowDisconnected || row.IsNetworkConnected) && (options.ShowAuxiliary || !row.IsVirtualNetwork);

    internal static ResourceRow? SelectionAfterFilter(IEnumerable<ResourceRow> visible, ResourceRow? previous)
    {
        var rows = visible.ToArray();
        return previous is not null && rows.Contains(previous) ? previous : rows.FirstOrDefault();
    }
}
