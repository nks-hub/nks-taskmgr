using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static void HardwareFactCaching()
    {
        var cache = new HardwareFactCache();
        void Apply(HardwareData data) => cache.Apply(data, FrameFlags.ProcessData | FrameFlags.ResourceData);
        WithHardwareFacts([("cpu", "Model", "Čip"), ("network", "Stav", "1"), ("network", "MTU", "1500")], data => {
            Apply(data);
            var cpu = cache.Values["cpu"]; var network = cache.Values["network"];
            for (int i = 0; i < 10; ++i) Apply(data);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; ++i) Apply(data);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(ReferenceEquals(cpu, cache.Values["cpu"]) && ReferenceEquals(network, cache.Values["network"])
                && allocated == 0, "Unchanged hardware facts allocated or replaced stable groups.");
        });
        var stableCpu = cache.Values["cpu"]; var previousNetwork = cache.Values["network"];
        WithHardwareFacts([("cpu", "Model", "Čip"), ("network", "Stav", "2"), ("network", "MTU", "1500")], Apply);
        Require(ReferenceEquals(stableCpu, cache.Values["cpu"]) && !ReferenceEquals(previousNetwork, cache.Values["network"])
            && previousNetwork["Stav"] == "1" && cache.Values["network"]["Stav"] == "2",
            "A dynamic fact mutated a retained group or replaced unrelated CPU facts.");

        WithHardwareFacts([("network", "MTU", "1500"), ("cpu", "Model", "Čip"), ("network", "Stav", "2")], Apply);
        Require(cache.Values["network"].Keys.SequenceEqual(["MTU", "Stav"]) && ReferenceEquals(stableCpu, cache.Values["cpu"]),
            "Interleaved hardware groups lost fact ordering or stable content.");
        WithHardwareFacts([("cpu", "Model", "Čip"), ("cpu", "Model", "Nový čip"), ("CPU", "Model", "Jiná skupina")], Apply);
        Require(cache.Values.Count == 2 && !cache.Values.ContainsKey("network") && cache.Values["cpu"].Count == 1
            && cache.Values["cpu"]["Model"] == "Nový čip" && cache.Values["CPU"]["Model"] == "Jiná skupina",
            "Removed hardware groups, duplicate labels or case-sensitive identities changed meaning.");
        WithHardwareFacts([("cpu", "", "")], Apply);
        Require(cache.Values.Count == 1 && cache.Values["cpu"].Count == 1 && cache.Values["cpu"][""] == "",
            "Removing facts retained obsolete values or lost empty strings.");
        WithHardwareFacts([], Apply);
        Require(cache.Values.Count == 0, "An empty inventory retained vanished hardware facts.");
        WithHardwareFacts([("cpu", "Model", "Obnovený čip")], Apply);
        GC.Collect(); GC.WaitForPendingFinalizers();
        Require(cache.Values["cpu"]["Model"] == "Obnovený čip", "Hardware data retained borrowed native memory.");
        var restoredCpu = cache.Values["cpu"];
        WithHardwareFacts([("network", "Stav", "1")], data => cache.Apply(data, FrameFlags.ResourceData));
        var restoredNetwork = cache.Values["network"];
        WithHardwareFacts([], data => cache.Apply(data, FrameFlags.ProcessErrorChanged));
        Require(ReferenceEquals(restoredCpu, cache.Values["cpu"]) && ReferenceEquals(restoredNetwork, cache.Values["network"]),
            "A resource-only or error-only frame removed facts from another source.");
        WithHardwareFacts([], data => cache.Apply(data, FrameFlags.ProcessData));
        Require(!cache.Values.ContainsKey("cpu") && ReferenceEquals(restoredNetwork, cache.Values["network"]),
            "A process-only inventory removed resource facts or retained vanished process facts.");
        WithHardwareFacts([("cpu", "Model", "Další čip")], data => cache.Apply(data, FrameFlags.ProcessData));
        WithHardwareFacts([], data => cache.Apply(data, FrameFlags.ResourceData));
        Require(cache.Values.Count == 1 && cache.Values["cpu"]["Model"] == "Další čip", "A resource-only inventory removed CPU facts.");
        App.Trace("Hardware fact cache passed: no unchanged allocations, stable groups, dynamic values, ordering, scoped disappearance and owned strings");
    }

    private static unsafe void WithHardwareFacts((string Id, string Name, string Value)[] facts, Action<HardwareData> apply)
    {
        string text = string.Concat(facts.Select(fact => fact.Id + fact.Name + fact.Value));
        var rows = new HardwareFactInfo[facts.Length];
        fixed (char* characters = text) {
            int offset = 0;
            for (int i = 0; i < facts.Length; ++i) {
                var fact = facts[i];
                rows[i].ResourceId = new String16 { Data = characters + offset, Length = (uint)fact.Id.Length }; offset += fact.Id.Length;
                rows[i].Name = new String16 { Data = characters + offset, Length = (uint)fact.Name.Length }; offset += fact.Name.Length;
                rows[i].Value = new String16 { Data = characters + offset, Length = (uint)fact.Value.Length }; offset += fact.Value.Length;
            }
            fixed (HardwareFactInfo* data = rows) apply(new HardwareData { Facts = data, FactCount = (uint)rows.Length });
        }
    }
}
