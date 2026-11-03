using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static unsafe void ResourceRefreshCaching()
    {
        var row = new ResourceRow("cache-network", ResourceKind.Network);
        fixed (char* title = "Network", description = "Adapter", status = "Připojeno") {
            var source = new ResourceInfo {
                Title = new String16 { Data = title, Length = 7 }, Description = new String16 { Data = description, Length = 7 },
                Status = new String16 { Data = status, Length = 9 }, Primary = new Metric { Value = 1, State = SampleState.Valid }
            };
            row.Update(source, 1, false, false);
            string previousTitle = row.Title, previousDescription = row.Description, previousStatus = row.Status;
            for (int i = 0; i < 10; ++i) row.Update(source, i, false, false);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; ++i) row.Update(source, i, false, false);
            Require(GC.GetAllocatedBytesForCurrentThread() == before && ReferenceEquals(previousTitle, row.Title)
                && ReferenceEquals(previousDescription, row.Description) && ReferenceEquals(previousStatus, row.Status),
                "Unchanged resource text allocated or replaced owned strings.");
            source.Primary = new Metric { State = SampleState.Error, Source = MetricSource.NetworkInterface };
            source.Secondary = new Metric { Value = 45, State = SampleState.Valid };
            source.Capacity = 2_000_000_000;
            row.Update(source, 200, false, true);
            Require(row.Primary.State == SampleState.Error && row.Secondary.Value == 45 && row.Capacity == 2_000_000_000
                && row.History.LastTime == 200 && row.Upload.LastValue.Value == 45,
                "Resource text reuse suppressed changed counters, invalid samples or history.");
        }
        fixed (char* title = "Renamed", status = "Odpojeno") {
            row.Update(new ResourceInfo { Title = new String16 { Data = title, Length = 7 },
                Status = new String16 { Data = status, Length = 8 } }, 201, false, false);
        }
        GC.Collect();
        Require(row.Title == "Renamed" && row.Description.Length == 0 && row.Status == "Odpojeno",
            "Changed or empty resource text retained borrowed native storage or obsolete values.");
        ResourceMembershipRefresh();
        App.Trace("Resource refresh reuse passed: stable owned text without allocations, changed metrics/history and network membership");
    }

    private static unsafe void ResourceMembershipRefresh()
    {
        using var model = new MonitorModel();
        model.SetResourceViewVisible(false);
        void Apply(bool connected, bool present = true) {
            fixed (char* id = "cache-network", title = "Network", status = connected ? "Připojeno" : "Odpojeno") {
                var source = new ResourceInfo { Kind = ResourceKind.Network, Id = new String16 { Data = id, Length = 13 },
                    Title = new String16 { Data = title, Length = 7 }, Status = new String16 { Data = status, Length = connected ? 9u : 8u } };
                model.Apply(new FrameView { Flags = FrameFlags.ResourceData,
                    Resources = new ResourceData { ResourceCount = present ? 1u : 0, Resources = &source } });
            }
        }
        void Show() { model.SetResourceViewVisible(false); model.SetResourceViewVisible(true); }
        Apply(true); Show();
        var original = model.Resources.Single();
        int changes = 0;
        model.Resources.CollectionChanged += (_, _) => ++changes;
        Apply(true); Show();
        Require(changes == 0 && ReferenceEquals(original, model.Resources.Single()),
            "An unchanged resource list replaced its rows.");
        Apply(false); Show();
        Require(model.Resources.Count == 0, "A disconnected interface stayed in the default connected-device list.");
        var hiddenPreview = original.Preview;
        original.History.Append(100_000_000_000, new Metric { Value = 10, State = SampleState.Valid });
        model.SetNetworkFilters(new NetworkFilterOptions(true, false));
        Require(ReferenceEquals(original, model.Resources.Single()) && !ReferenceEquals(hiddenPreview, original.Preview),
            "Showing disconnected devices lost the existing row or retained its hidden preview without another tick.");
        model.SetNetworkFilters(default);
        Apply(true); Show();
        Require(ReferenceEquals(original, model.Resources.Single()), "A reconnected interface stayed hidden behind cached membership.");
        Apply(true, false); Show();
        Require(model.Resources.Count == 0, "A disappeared interface stayed in the cached resource list.");
        Apply(true); Show();
        Require(!ReferenceEquals(original, model.Resources.Single()), "A recreated interface reused a removed resource row.");
    }
}
