using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static void ResourceNotifications()
    {
        UnchangedResourceNotificationCost(); ResourceMetadataNotifications();
        App.Trace("Resource notifications passed: unchanged0; metadata/metric/state/source changes1; mutable network flags; deferred raw publication; history rebinding and hidden activation");
    }

    private static void UnchangedResourceNotificationCost()
    {
        var row = new ResourceRow("notification-cost", ResourceKind.Network);
        var receive = new Metric { Value = 2000000, State = SampleState.Valid, Source = MetricSource.NetworkInterface };
        var upload = receive with { Value = 3000000 };
        row.Update("Network", "Adapter", "Připojeno", receive, upload, default, 100000000, 1000000000);
        int notifications = 0; row.PropertyChanged += (_, _) => ++notifications;
        for (int i = 0; i < 16; ++i) row.RefreshPreview();
        notifications = 0; long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; ++i) row.RefreshPreview();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before; int unchangedEvents = notifications;
        Require(allocated == 0 && unchangedEvents == 0, "Subscribed unchanged resource RefreshPreview allocated or sent a redundant event.");
        row.Update("Network", "Adapter", "Připojeno", receive with { Value = 4000000 }, upload, default, 100000000, 1000000000, recordHistory: false);
        int updateEvents = notifications; row.RefreshPreview(); int followingEvents = notifications - updateEvents;
        Require(updateEvents == 1 && followingEvents == 0 && row.Primary.Value == 4000000,
            "The actual Update plus following RefreshPreview lost a changed value or notified it twice.");
        App.Trace(FormattableString.Invariant($"Resource notification cost: scope=actual-row;unchangedCalls=100;unchangedEvents={unchangedEvents};unchangedAllocatedBytes={allocated};updateEvents={updateEvents};followingRefreshEvents={followingEvents};baselineSourceNotifications=2;baselineRuntimeMeasured=False;wholeAppCpuMeasured=False"));
    }

    private static void ResourceMetadataNotifications()
    {
        var row = new ResourceRow("notification-metadata", ResourceKind.Network);
        string title = "Network", description = "Adapter", status = "Připojeno";
        var primary = new Metric { Value = 2000000, State = SampleState.Valid, Source = MetricSource.NetworkInterface };
        var secondary = primary with { Value = 3000000 }; var tertiary = primary with { Value = 1 }; double capacity = 100000000;
        void Apply(bool notify = true) => row.Update(title, description, status, primary, secondary, tertiary, capacity, 1000000000,
            notify: notify, recordHistory: false);
        row.Update(title, description, status, primary, secondary, tertiary, capacity, 1000000000); var preview = row.Preview;
        Require(preview is not null, "Resource notification fixture has no real history geometry.");
        int notifications = 0; row.PropertyChanged += (_, _) => ++notifications;
        void Change(Action change, string field) {
            int before = notifications; change(); Apply(); int published = notifications; row.RefreshPreview();
            Require(published == before + 1 && notifications == published && ReferenceEquals(preview, row.Preview),
                "A changed resource " + field + " was suppressed, duplicated or rebuilt unchanged history geometry.");
        }
        title = new string(title.ToCharArray()); description = new string(description.ToCharArray()); status = new string(status.ToCharArray());
        Apply(); row.RefreshPreview(); Require(notifications == 0, "Equal resource text values were treated as changed object references.");
        Change(() => title = "Renamed network", "title");
        Change(() => description = "Adapter B", "description");
        Change(() => status = "Odpojeno", "status");
        Change(() => primary = primary with { Value = 4000000 }, "primary value");
        Change(() => secondary = secondary with { Value = 5000000 }, "secondary value");
        Change(() => tertiary = tertiary with { Value = 2 }, "tertiary value");
        Change(() => capacity = 200000000, "capacity");
        Change(() => primary = primary with { State = SampleState.Unavailable }, "primary state");
        Change(() => primary = primary with { State = SampleState.Valid }, "primary recovery");
        string networkValue = row.Value;
        Change(() => secondary = secondary with { State = SampleState.AccessDenied }, "secondary state");
        Require(row.Value != networkValue, "Secondary measurement state did not reach the bound network value.");
        Change(() => tertiary = tertiary with { State = SampleState.First }, "tertiary state");
        Change(() => primary = primary with { Source = MetricSource.PerformanceCounter }, "primary source");
        Change(() => secondary = secondary with { Source = MetricSource.PerformanceCounter }, "secondary source");
        Change(() => tertiary = tertiary with { Source = MetricSource.PerformanceCounter }, "tertiary source");

        int previous = notifications; row.SetStale(true); row.RefreshPreview();
        Require(notifications == previous + 1 && row.Stale && row.Value == "Zastaralá data", "An unchanged refresh repeated a published stale notification.");
        row.SetStale(true); Require(notifications == previous + 1, "An unchanged stale flag notified twice.");
        previous = notifications; row.SetStale(false, notify: false);
        Require(notifications == previous && !row.Stale, "A silent stale transition published a current binding update.");
        row.RefreshPreview(); row.RefreshPreview(); Require(notifications == previous + 1, "Deferred stale recovery was lost or duplicated.");

        var facts = new Dictionary<string, string> { ["Stav rozhraní Windows"] = "1", ["Druh rozhraní"] = "Fyzické" };
        previous = notifications; row.HardwareFacts = facts; row.RefreshPreview();
        Require(notifications == previous + 1 && row.IsNetworkConnected && !row.IsVirtualNetwork, "Changed bound network flags were hidden by resource caching.");
        previous = notifications; facts["Stav rozhraní Windows"] = "2"; row.RefreshPreview();
        Require(notifications == previous + 1 && !row.IsNetworkConnected, "An in-place network operation-state change was not published.");
        previous = notifications; facts["Druh rozhraní"] = "Virtuální"; row.RefreshPreview();
        Require(notifications == previous + 1 && row.IsVirtualNetwork, "An in-place network classifier change was not published.");
        previous = notifications; facts["MTU"] = "1500"; row.RefreshPreview(); row.RefreshPreview();
        Require(notifications == previous, "An unrelated hardware fact notified unchanged bound resource values.");

        title = "Deferred title"; primary = primary with { Value = 9000000 }; previous = notifications; Apply(notify: false);
        row.SetStale(true, notify: false);
        Require(notifications == previous && row.Title == title && row.Primary.Value == primary.Value && row.Stale,
            "A silent raw resource update was dropped or published before visible return.");
        row.RefreshPreview(); row.RefreshPreview();
        Require(notifications == previous + 1 && ReferenceEquals(preview, row.Preview), "Deferred raw metadata was not published once without fake history changes.");
        row.SetStale(false); previous = notifications;
        title = "Pending title"; Apply(notify: false); row.SetStale(true);
        Require(notifications == previous + 1, "A stale value notification was suppressed by pending metadata.");
        row.RefreshPreview(); row.RefreshPreview();
        Require(notifications == previous + 2 && row.Title == title, "Publishing stale state consumed or duplicated separately pending metadata.");
    }
}
