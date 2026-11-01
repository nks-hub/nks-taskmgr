using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static class SnapshotComparisonChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static Metric Valid(double value = 0) => new() { Value = value, State = SampleState.Valid, Source = MetricSource.NativeProcess };
    private static SnapshotDocument Fixture() => new() {
        CapturedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), CapturedMonotonicNs = 4_000_000_000,
        ProcessTimeNs = 3_500_000_000, ProcessWallTimeUnixNs = 9_000_000_000, ResourceTimeNs = 3_600_000_000,
        Boot = new BootIdentityInfo { Low = 1, High = 2, State = SampleState.Valid }, LogicalProcessorCount = 2, Cpu = Valid(),
        Memory = new MemoryInfo { State = SampleState.Valid, PhysicalBytes = ulong.MaxValue, AvailableBytes = 9007199254740993, PageFileState = SampleState.Unavailable },
        Processes = [new(new(new(1, 2), 42, GenerationKind.CreationTime, ulong.MaxValue - 1), "example.exe", 4, 3, 6,
            ulong.MaxValue - 2, 9007199254740993, Valid(), new() { State = SampleState.AccessDenied }, Valid(), false)],
        Processors = [new(0, 0, Valid()), new(0, 1, Valid())]
    };

    private static byte[] Export(SnapshotDocument snapshot, SnapshotFields fields = SnapshotFields.Default)
    {
        using var stream = new MemoryStream(); SnapshotExport.Write(stream, snapshot, fields); return stream.ToArray();
    }
    private static JsonObject Node(byte[] json) => JsonNode.Parse(json)!.AsObject();
    private static JsonObject Process(JsonObject value) => value["processes"]![0]!.AsObject();
    private static ImportedSnapshot Import(JsonObject node) => SnapshotComparison.Read(Encoding.UTF8.GetBytes(node.ToJsonString()));
    private static ComparedMetric Metric(ProcessComparison process, string name) => process.Metrics.Single(metric => metric.Name == name);
    private static void Reject(byte[] json, string message)
    {
        try { SnapshotComparison.Read(json); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void RejectMutation(byte[] original, Action<JsonObject> mutate, string message)
    {
        var value = Node(original); mutate(value); Reject(Encoding.UTF8.GetBytes(value.ToJsonString()), message);
    }

    public static async Task RunAsync()
    {
        var fixture = Fixture();
        byte[] exported = Export(fixture);
        var before = SnapshotComparison.Read(exported);
        Require(before.Processes.Count == 1 && before.Processes[0].Name is null && before.Processes[0].IdentityVerified,
            "Defaultní export bez názvů nelze použít.");
        Require(before.Processes[0].Key.Generation == ulong.MaxValue - 1
            && before.Processes[0].Metrics["workingSet"].UnsignedValue == ulong.MaxValue - 2
            && before.Processes[0].Metrics["privateMemory"].UnsignedValue == 9007199254740993,
            "Načtení zaokrouhlilo UInt64.");
        var same = SnapshotComparison.Compare(before, before).Processes.Single();
        Require(same.Kind == ProcessComparisonKind.Unchanged && Metric(same, "cpu").FloatingDelta == 0
            && Metric(same, "workingSet").IntegerDelta == 0 && Metric(same, "readIo").FloatingDelta is null,
            "Platná nula nebo nedostupná metrika změnila význam.");
        var changedDoc = fixture with { Processes = [fixture.Processes[0] with { WorkingBytes = ulong.MaxValue, PrivateBytes = 9007199254740994, Cpu = Valid(1.25) }] };
        var after = SnapshotComparison.Read(Export(changedDoc));
        var changed = SnapshotComparison.Compare(before, after).Processes.Single();
        Require(changed.Kind == ProcessComparisonKind.Changed && Metric(changed, "workingSet").IntegerDelta == 2
            && Metric(changed, "privateMemory").IntegerDelta == 1 && Metric(changed, "cpu").FloatingDelta == 1.25,
            "Rozdíl ztratil přesnost nebo změněný proces.");
        var reverse = SnapshotComparison.Compare(after, before).Processes.Single();
        Require(Metric(reverse, "workingSet").IntegerDelta == -2, "Pokles UInt64 přetekl.");
        var reused = fixture with { Processes = [fixture.Processes[0] with { Key = fixture.Processes[0].Key with { Generation = ulong.MaxValue } }] };
        var reusedRows = SnapshotComparison.Compare(before, SnapshotComparison.Read(Export(reused))).Processes;
        Require(reusedRows.Count == 2 && reusedRows.Any(row => row.Kind == ProcessComparisonKind.Created)
            && reusedRows.Any(row => row.Kind == ProcessComparisonKind.Ended), "Opakovaný PID sloučil jiné životnosti.");
        var anotherKind = fixture with { Processes = [fixture.Processes[0] with { Key = fixture.Processes[0].Key with { Kind = GenerationKind.SequenceNumber } }] };
        var kindRows = SnapshotComparison.Compare(before, SnapshotComparison.Read(Export(anotherKind))).Processes;
        Require(kindRows.Count == 2 && kindRows.All(row => row.Kind == ProcessComparisonKind.NotComparable
            && row.Reason == SnapshotComparisonReason.GenerationKindChanged), "Jiný druh generace byl vydáván za stejnou životnost nebo konec.");
        var reboot = fixture with { Boot = new BootIdentityInfo { Low = 9, High = 8, State = SampleState.Valid },
            Processes = [fixture.Processes[0] with { Key = fixture.Processes[0].Key with { Boot = new(9, 8) } }] };
        var rebootResult = SnapshotComparison.Compare(before, SnapshotComparison.Read(Export(reboot)));
        Require(rebootResult.Processes.Count == 2 && rebootResult.Processes.All(row => row.Reason == SnapshotComparisonReason.BootChanged)
            && rebootResult.Before.Metadata.BootIdentifier != rebootResult.After.Metadata.BootIdentifier,
            "Změna bootu ztratila řádky nebo vysvětlení.");
        var clock = fixture with { CapturedAt = fixture.CapturedAt.AddYears(-1), ProcessWallTimeUnixNs = -1 };
        var clockResult = SnapshotComparison.Compare(before, SnapshotComparison.Read(Export(clock)));
        Require(clockResult.Processes.Single().Kind == ProcessComparisonKind.Unchanged
            && clockResult.After.Metadata.CapturedAtUtc < clockResult.Before.Metadata.CapturedAtUtc
            && clockResult.After.Metadata.ProcessMeasuredAtUnixNs == -1, "Změna hodin přehodila soubory nebo identitu.");
        foreach (Action<JsonObject> weaken in new Action<JsonObject>[] {
            value => Process(value)["generationValue"] = "0",
            value => { Process(value)["generationKind"] = "Missing"; Process(value)["generationValue"] = "0"; },
            value => Process(value)["bootIdentifier"] = new string('0', 32),
            value => Process(value)["bootIdentifier"] = "00000000000000090000000000000008",
            value => Process(value)["identityReliable"] = false,
            value => { value["bootIdentityState"] = "AccessDenied"; value["bootIdentifier"] = null; }
        }) {
            var weakNode = Node(exported); weaken(weakNode);
            var result = SnapshotComparison.Compare(before, Import(weakNode));
            Require(result.Processes.Count == 2 && result.Processes.All(row => row.Kind == ProcessComparisonKind.NotComparable),
                "Neověřená identita byla spárována pouze podle PID.");
        }
        var stale = SnapshotComparison.Compare(before, SnapshotComparison.Read(Export(fixture with { Stale = true }))).Processes.Single();
        Require(stale.Kind == ProcessComparisonKind.Changed && Metric(stale, "cpu").Before!.State == SampleState.Valid
            && Metric(stale, "cpu").After!.State == SampleState.Stale && Metric(stale, "cpu").FloatingDelta is null
            && Metric(stale, "workingSet").IntegerDelta is null, "Zastaralá metrika vytvořila nulový rozdíl.");
        var errorNode = Node(exported); errorNode["processStreamState"] = "Error"; errorNode["processes"] = new JsonArray(); errorNode["processesTotal"] = 0;
        Require(SnapshotComparison.Compare(before, Import(errorNode)).Processes.Single().Reason == SnapshotComparisonReason.IncompleteProcessStream,
            "Chybný prázdný proud vypadal jako konec procesu.");
        var minimal = SnapshotComparison.Read(Export(fixture, 0));
        var selected = SnapshotComparison.Compare(before, minimal).Processes.Single();
        Require(Metric(selected, "cpu").After is null && Metric(selected, "cpu").FloatingDelta is null
            && Metric(selected, "workingSet").After is null && Metric(selected, "workingSet").IntegerDelta is null,
            "Nevybrané pole vytvořilo nulový rozdíl.");
        foreach (Action<JsonObject> alter in new Action<JsonObject>[] {
            value => Process(value)["cpu"]!["unit"] = "percent",
            value => Process(value)["cpu"]!["source"] = "ProcessorTime"
        }) {
            var value = Node(exported); alter(value);
            Require(Metric(SnapshotComparison.Compare(before, Import(value)).Processes.Single(), "cpu").FloatingDelta is null,
                "Neslučitelné jednotky nebo zdroje vytvořily rozdíl.");
        }
        var stateNode = Node(exported); Process(stateNode)["readIo"]!["state"] = "Unsupported";
        var stateChange = Metric(SnapshotComparison.Compare(before, Import(stateNode)).Processes.Single(), "readIo");
        Require(stateChange.Changed && stateChange.Before!.State == SampleState.AccessDenied && stateChange.After!.State == SampleState.Unsupported
            && stateChange.FloatingDelta is null, "Změna měřeného stavu se ztratila.");
        var named = SnapshotComparison.Read(Export(fixture, SnapshotFields.Default | SnapshotFields.Names));
        Require(named.Processes[0].Name == "example.exe", "Název se nenačetl ze souboru.");
        var renamed = fixture with { Processes = [fixture.Processes[0] with { Name = "renamed.exe" }] };
        Require(SnapshotComparison.Compare(named, SnapshotComparison.Read(Export(renamed, SnapshotFields.Default | SnapshotFields.Names)))
            .Processes.Single().Kind == ProcessComparisonKind.Changed, "Změna názvu ze souboru se ztratila.");

        RejectMutation(exported, value => value["schemaVersion"] = 2, "Neznámá verze byla přijata.");
        Reject(Encoding.UTF8.GetBytes(SnapshotExport.Preview(fixture, SnapshotFields.Default)), "Náhled byl přijat.");
        RejectMutation(exported, value => { value["processes"]!.AsArray().Add(Process(value).DeepClone()); value["processesTotal"] = 2; }, "Duplicitní životnost byla přijata.");
        foreach (string changedIdentity in new[] { "generationValue", "generationKind" })
            RejectMutation(exported, value => {
                var duplicate = Process(value).DeepClone();
                duplicate[changedIdentity] = changedIdentity == "generationValue" ? "1" : "SequenceNumber";
                value["processes"]!.AsArray().Add(duplicate); value["processesTotal"] = 2;
            }, "Jeden inventář přijal různé životnosti téhož PID.");
        RejectMutation(exported, value => Process(value).Remove("generationValue"), "Neúplná identita byla přijata.");
        RejectMutation(exported, value => Process(value)["generationKind"] = "FutureKind", "Neznámý druh generace byl přijat.");
        RejectMutation(exported, value => Process(value)["generationValue"] = "18446744073709551616", "Přetečená generace byla přijata.");
        RejectMutation(exported, value => Process(value)["pid"] = 4294967296UL, "Přetečený PID byl přijat.");
        RejectMutation(exported, value => Process(value)["bootIdentifier"] = "not-a-boot-id", "Vadná boot identita byla přijata.");
        RejectMutation(exported, value => Process(value).Remove("cpu"), "Chybějící vybraná metrika byla přijata.");
        RejectMutation(exported, value => Process(value)["cpu"]!["state"] = null, "Prázdný stav byl přijat.");
        RejectMutation(exported, value => Process(value)["cpu"]!["value"] = null, "Platná metrika bez hodnoty byla přijata.");
        RejectMutation(exported, value => Process(value)["cpu"]!["value"] = -1, "Záporná platná metrika byla přijata.");
        RejectMutation(exported, value => Process(value)["readIo"]!["value"] = 0, "Neplatný stav s hodnotou byl přijat.");
        RejectMutation(exported, value => Process(value)["workingSet"]!["value"] = 1.5, "Paměť s desetinnou hodnotou byla přijata.");
        RejectMutation(exported, value => Process(value)["cpu"]!["unit"] = "unknown", "Neznámá jednotka byla přijata.");
        RejectMutation(exported, value => Process(value)["cpu"]!["source"] = "FutureSource", "Neznámý zdroj byl přijat.");
        RejectMutation(exported, value => value["processesTotal"] = 2, "Neúplný počet procesů byl přijat.");
        string raw = Encoding.UTF8.GetString(exported);
        Reject(Encoding.UTF8.GetBytes(raw.Replace("\"pid\": 42", "\"pid\": 42, \"pid\": 43", StringComparison.Ordinal)), "Duplicitní vlastnost byla přijata.");
        var infinite = Node(exported); Process(infinite)["cpu"]!["value"] = "INFINITY_SENTINEL";
        Reject(Encoding.UTF8.GetBytes(infinite.ToJsonString().Replace("\"INFINITY_SENTINEL\"", "1e999", StringComparison.Ordinal)), "Nekonečná metrika byla přijata.");
        Reject(Encoding.UTF8.GetBytes(raw[..^1] + ",\"nested\":" + new string('[', SnapshotComparison.MaximumDepth + 1)
            + "0" + new string(']', SnapshotComparison.MaximumDepth + 1) + "}"), "Překročená hloubka byla přijata.");
        Reject(Encoding.UTF8.GetBytes(raw[..^1] + ",\"extra\":[" + string.Join(',', Enumerable.Repeat("0", SnapshotComparison.MaximumArrayItems + 1))
            + "]}"), "Překročený celkový počet prvků byl přijat.");
        Reject(Export(fixture with { Processes = Enumerable.Range(0, SnapshotComparison.MaximumProcesses + 1)
            .Select(index => fixture.Processes[0] with { Key = fixture.Processes[0].Key with { Pid = (uint)index } }).ToArray() }, 0),
            "Překročený počet procesů byl přijat.");
        Reject(Export(fixture with { Processors = Enumerable.Range(0, SnapshotComparison.MaximumProcessors + 1)
            .Select(index => new SnapshotProcessor(0, (uint)index, Valid())).ToArray() }), "Překročený počet CPU byl přijat.");
        var gpu = new SnapshotResource("gpu-demo", ResourceKind.Gpu, "GPU", "Demo", Valid(), Valid(), Valid(),
            new() { State = SampleState.Unsupported, Source = MetricSource.GpuAdapter }, false, []);
        var gpuDocument = fixture with { Resources = [gpu with { Engines = Enumerable.Range(0, SnapshotComparison.MaximumEngines)
            .Select(index => new SnapshotEngine("engine-" + index, "3D", Valid())).ToArray() }] };
        SnapshotComparison.Read(Export(gpuDocument));
        Reject(Export(gpuDocument with { Resources = [gpuDocument.Resources[0] with { Engines = [..gpuDocument.Resources[0].Engines, new("extra", "Copy", Valid())] }] }),
            "Překročený počet GPU enginů byl přijat.");
        Reject(Export(fixture with { Resources = Enumerable.Repeat(gpu, SnapshotComparison.MaximumDevices + 1).ToArray() }),
            "Překročený počet zařízení byl přijat.");
        RejectMutation(Export(fixture with { Resources = [gpu] }), value => value["devices"]![0]!["utilization"]!["state"] = null,
            "Vadná metrika v sekci zařízení byla přijata.");
        Reject(Export(fixture with { Processes = [fixture.Processes[0] with { Name = new string('x', SnapshotComparison.MaximumStringBytes + 1) }] },
            SnapshotFields.Default | SnapshotFields.Names), "Překročený text byl přijat.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { SnapshotComparison.Read(exported, cancellation.Token); throw new InvalidOperationException("Načtení ignorovalo zrušení."); } catch (OperationCanceledException) { }
        try { SnapshotComparison.Compare(before, after, cancellation.Token); throw new InvalidOperationException("Porovnání ignorovalo zrušení."); } catch (OperationCanceledException) { }
        try { await SnapshotComparison.CompareAsync(before, after, cancellation.Token); throw new InvalidOperationException("Asynchronní porovnání ignorovalo zrušení."); } catch (OperationCanceledException) { }
        string directory = Path.Combine(Path.GetTempPath(), "nks-comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            string file = Path.Combine(directory, "export.json");
            await File.WriteAllBytesAsync(file, exported);
            Require((await SnapshotComparison.ReadAsync(file)).Processes[0].Key == before.Processes[0].Key, "Načtení skutečného souboru změnilo identitu.");
            try { await SnapshotComparison.ReadAsync(file, cancellation.Token); throw new InvalidOperationException("Čtení souboru ignorovalo zrušení."); } catch (OperationCanceledException) { }
            using (var large = new FileStream(file, FileMode.Create, FileAccess.Write)) large.SetLength(SnapshotComparison.MaximumFileBytes + 1L);
            try { await SnapshotComparison.ReadAsync(file); throw new InvalidOperationException("Nadměrný soubor byl přijat."); } catch (InvalidDataException) { }
            await MeasureTenThousandAsync(directory, fixture);
        } finally { foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        Console.WriteLine("PASS SnapshotComparison: export, identity, metrics, malformed input, limits, cancellation");
    }

    private static async Task MeasureTenThousandAsync(string directory, SnapshotDocument fixture)
    {
        var processes = Enumerable.Range(0, 10_000).Select(index => fixture.Processes[0] with {
            Key = fixture.Processes[0].Key with { Pid = (uint)index }, WorkingBytes = 9007199254740993UL + (uint)index }).ToArray();
        string leftPath = Path.Combine(directory, "before-10000.json"), rightPath = Path.Combine(directory, "after-10000.json");
        await File.WriteAllBytesAsync(leftPath, Export(fixture with { Processes = processes }));
        await File.WriteAllBytesAsync(rightPath, Export(fixture with { Processes = processes.Select(process => process with { WorkingBytes = process.WorkingBytes + 1 }).ToArray() }));
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long managedBefore = GC.GetTotalMemory(false);
        var watch = Stopwatch.StartNew();
        var before = await SnapshotComparison.ReadAsync(leftPath); var after = await SnapshotComparison.ReadAsync(rightPath);
        double readMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); var comparison = await SnapshotComparison.CompareAsync(before, after); double compareMs = watch.Elapsed.TotalMilliseconds;
        Require(comparison.Processes.Count == 10_000 && comparison.Processes.All(process => process.Kind == ProcessComparisonKind.Changed
            && Metric(process, "workingSet").IntegerDelta == 1), "Deset tisíc procesů ztratilo přesné rozdíly.");
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        Console.WriteLine(FormattableString.Invariant($"MEASURE rows=10000;readMs={readMs:F3};compareMs={compareMs:F3};managedGrowthBytes={GC.GetTotalMemory(false) - managedBefore};workingSetBytes={current.WorkingSet64};peakWorkingSetBytes={current.PeakWorkingSet64};fileBytes={new FileInfo(leftPath).Length}"));
    }
}
