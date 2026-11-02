using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static void NetworkFilters()
    {
        static ResourceRow Network(string id, string classification, bool connected, bool typedState = true)
        {
            var row = new ResourceRow(id, ResourceKind.Network) {
                HardwareFacts = new Dictionary<string, string> { ["Druh rozhraní"] = classification }
            };
            if (typedState) row.HardwareFacts = new Dictionary<string, string>(row.HardwareFacts) {
                ["Stav rozhraní Windows"] = connected ? "1" : "2"
            };
            row.Update(id, "", connected ? "Připojeno" : "Odpojeno", new Metric { Value = 42, State = SampleState.Valid },
                new Metric { Value = 7, State = SampleState.Valid }, default, 1_000_000, 1_000_000_000, notify: false);
            return row;
        }

        var cpu = new ResourceRow("cpu", ResourceKind.Cpu);
        var ethernet = Network("ethernet", "Fyzické", true);
        var wifi = Network("wifi-offline", "Fyzické", false);
        var vpn = Network("wan-ikev2", "VPN", true);
        var ppp = Network("ppp-online", "PPP", true);
        var wintun = Network("wintun-offline", "VPN", false);
        var auxiliary = Network("vswitch-online", "Pomocné / virtuální", true);
        var wan = Network("wan-inactive", "Pomocné / virtuální", false);
        var legacy = Network("legacy-virtual", "Virtuální", true, typedState: false);
        var legacyPhysical = Network("legacy-physical", "Fyzické", true, typedState: false);
        ResourceRow[] all = [cpu, ethernet, wifi, vpn, ppp, wintun, auxiliary, wan, legacy, legacyPhysical];
        string[] Visible(NetworkFilterOptions options) => all.Where(row => NetworkResourceFilter.Includes(row, options)).Select(row => row.Id).ToArray();

        Require(Visible(default).SequenceEqual(["cpu", "ethernet", "wan-ikev2", "ppp-online", "legacy-physical"]),
            "Default network view lost a connected physical/PPP/VPN row or included an offline/auxiliary row.");
        Require(Visible(new(true, false)).SequenceEqual(["cpu", "ethernet", "wifi-offline", "wan-ikev2", "ppp-online", "wintun-offline", "legacy-physical"]),
            "Disconnected network selection hid VPNs or exposed auxiliary interfaces.");
        Require(Visible(new(false, true)).SequenceEqual(["cpu", "ethernet", "wan-ikev2", "ppp-online", "vswitch-online", "legacy-virtual", "legacy-physical"]),
            "Auxiliary selection exposed disconnected interfaces or lost a legacy virtual row.");
        Require(Visible(new(true, true)).SequenceEqual(all.Select(row => row.Id)), "Both network options lost raw inventory rows.");
        var visible = all.Where(row => NetworkResourceFilter.Includes(row, default)).ToArray();
        Require(ReferenceEquals(NetworkResourceFilter.SelectionAfterFilter(visible, vpn), vpn)
            && ReferenceEquals(NetworkResourceFilter.SelectionAfterFilter(visible, wifi), cpu)
            && ReferenceEquals(NetworkResourceFilter.SelectionAfterFilter(visible, null), cpu)
            && NetworkResourceFilter.SelectionAfterFilter([], vpn) is null, "Network filtering retained a hidden selection or lost a visible selection.");
        Require(all.Length == 10 && wintun.History.LastValue.Value == 42 && wintun.Upload.LastValue.Value == 7,
            "Filtering changed hidden resource inventory or history.");
        ethernet.SetStale(true, notify: false);
        Require(NetworkResourceFilter.Includes(ethernet, default), "A stale sample hid a connected interface.");
        foreach (var options in new NetworkFilterOptions[] { default, new(false, true), new(true, false), new(true, true) })
            Require(NetworkViewPreference.Parse(NetworkViewPreference.Format(options)) == options, "Network preferences did not round-trip both options.");
        Require(NetworkViewPreference.Parse("all") == new NetworkFilterOptions(false, true)
            && NetworkViewPreference.Parse("physical") == default && NetworkViewPreference.Parse("invalid") == default,
            "Legacy network preferences lost the auxiliary choice or enabled disconnected interfaces.");
        App.Trace("Network filters passed: connected physical/PPP/VPN, disconnected and auxiliary options, legacy settings, selection fallback and retained history");
    }
}
