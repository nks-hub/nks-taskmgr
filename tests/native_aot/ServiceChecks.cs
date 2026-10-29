using NksTaskMgr.Native;
using System.Diagnostics;

internal static class ServiceChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static unsafe void Verify()
    {
        Require(sizeof(ServiceConfigInfo) == 80 && sizeof(ServiceInfo) == 128 && sizeof(ServiceDetailInfo) == 168 && sizeof(ServiceFrameView) == 248, "Managed service ABI layout mismatch.");
        using var services = NativeServices.Open();
        using var inactive = services.Read();
        Require(inactive is null, "Inactive service monitor produced a frame.");
        bool rejected = false;
        try { services.Select("bad\0name"); } catch (NativeMonitorException error) when (error.Result == NativeResult.InvalidArgument) { rejected = true; }
        Require(rejected, "Embedded service terminator was accepted.");
        services.Select("RpcSs"); services.SetActive(true);
        NativeServiceFrame? retained = null;
        string account = string.Empty;
        uint count = 0;
        var watch = Stopwatch.StartNew();
        try {
            while (watch.Elapsed < TimeSpan.FromSeconds(10) && (retained is null || count == 0)) {
                using var frame = services.Read();
                frame?.Read(view => {
                    Require(view.AbiVersion == 1 && view.Error.Length == 0 && view.DetailError.Length == 0, "Managed service collection reported an error.");
                    if ((view.Flags & ServiceFrameFlags.Data) != 0) {
                        count = view.Data.ServiceCount;
                        Require(view.Data.MeasuredAtNs > 0 && view.Data.MeasuredAtNs <= view.ReadAtNs, "Service clocks differ.");
                        for (uint i = 0; i < count; ++i) Require(view.Data.Services[i].Name.Copy().Length > 0, "Service name was lost.");
                    }
                    if ((view.Flags & ServiceFrameFlags.Detail) != 0) {
                        Require(view.Detail.Name.Copy() == "RpcSs" && view.Detail.StatusError == 0 && view.Detail.Config.Error == 0, "Wrong managed service detail.");
                        Require(view.Detail.MeasuredAtNs > 0 && view.Detail.MeasuredAtNs <= view.ReadAtNs, "Service detail clock differs.");
                        account = view.Detail.Config.Account.Copy(); retained ??= frame!.Clone();
                    }
                });
                Thread.Sleep(20);
            }
            Require(count > 0 && retained is not null && account.Length > 0, "Managed service streams did not arrive.");
            services.Dispose(); GC.Collect(); GC.WaitForPendingFinalizers();
            retained!.Read(view => Require(view.Detail.Config.Account.Copy() == account && view.Detail.Name.Copy() == "RpcSs", "Managed service frame lost its storage."));
            Console.WriteLine($"Service ABI passed: {count} services; retained detail survived monitor disposal.");
        } finally { retained?.Dispose(); }
    }
}
