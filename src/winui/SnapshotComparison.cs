using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal enum ProcessComparisonKind { Created, Ended, Changed, Unchanged, NotComparable }
internal enum SnapshotComparisonReason { None, BootNotVerified, BootChanged, WeakIdentity, GenerationKindChanged, IncompleteProcessStream }

internal sealed record ImportedMetric(string Unit, MetricSource Source, SampleState State, double? Value = null, ulong? UnsignedValue = null);
internal sealed record ImportedProcess(ProcessKey Key, bool IdentityVerified, string? Name, uint ParentPid,
    IReadOnlyDictionary<string, ImportedMetric> Metrics);
internal sealed record SnapshotMetadata(DateTimeOffset CapturedAtUtc, long CapturedMonotonicNs,
    long ProcessMeasuredAtMonotonicNs, long ProcessMeasuredAtUnixNs, long ResourceMeasuredAtMonotonicNs,
    SampleState ProcessStreamState, SampleState ResourceStreamState, SampleState BootIdentityState,
    BootIdentifier? BootIdentifier, bool Paused, bool Stale, IReadOnlySet<string> Fields);
internal sealed record ImportedSnapshot(SnapshotMetadata Metadata, IReadOnlyList<ImportedProcess> Processes);
internal sealed record ComparedMetric(string Name, ImportedMetric? Before, ImportedMetric? After,
    double? FloatingDelta, decimal? IntegerDelta)
{
    public bool Changed => Before != After;
}
internal sealed record ProcessComparison(ProcessComparisonKind Kind, SnapshotComparisonReason Reason,
    ImportedProcess? Before, ImportedProcess? After, IReadOnlyList<ComparedMetric> Metrics);
internal sealed record SnapshotComparisonResult(ImportedSnapshot Before, ImportedSnapshot After,
    IReadOnlyList<ProcessComparison> Processes);

internal static class SnapshotComparison
{
    public const int MaximumFileBytes = 64 * 1024 * 1024;
    public const int MaximumDepth = 16;
    public const int MaximumProcesses = 50_000;
    public const int MaximumDevices = 4096;
    public const int MaximumProcessors = 4096;
    public const int MaximumEngines = 256;
    public const int MaximumArrayItems = 100_000;
    public const int MaximumStringBytes = 256 * 1024;
    public const int MaximumTokens = 8_000_000;

    public static Task<ImportedSnapshot> ReadAsync(string path, CancellationToken cancel = default) => Task.Run(() => {
        cancel.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131_072, FileOptions.SequentialScan);
        if (stream.Length < 2 || stream.Length > MaximumFileBytes) throw Invalid("Soubor má nepovolenou velikost.");
        var bytes = new byte[checked((int)stream.Length)];
        int offset = 0;
        while (offset < bytes.Length) {
            cancel.ThrowIfCancellationRequested();
            int count = stream.Read(bytes, offset, Math.Min(131_072, bytes.Length - offset));
            if (count == 0) throw Invalid("Soubor se během čtení zkrátil.");
            offset += count;
        }
        if (stream.ReadByte() != -1) throw Invalid("Soubor se během čtení zvětšil.");
        return Read(bytes, cancel);
    }, cancel);

    internal static ImportedSnapshot Read(ReadOnlyMemory<byte> json, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        if (json.Length < 2 || json.Length > MaximumFileBytes) throw Invalid("Soubor má nepovolenou velikost.");
        try {
            Preflight(json.Span, cancel);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            ValidateProperties(document.RootElement, cancel);
            var root = Object(document.RootElement);
            if (UInt(root, "schemaVersion") != 1) throw Invalid("Nepodporovaná verze snímku.");
            if (root.TryGetProperty("previewOnly", out var preview) && Boolean(preview)) throw Invalid("Náhled nelze porovnávat.");
            if (Text(root, "application") != "NKS Task Manager") throw Invalid("Neznámý formát snímku.");
            var fields = Array(root, "fields").EnumerateArray().Select(Text).ToArray();
            if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Length || fields.Any(field => field is not
                ("Cpu" or "Memory" or "Io" or "Devices" or "Names" or "DeviceIds"))) throw Invalid("Neplatný seznam polí.");
            bool names = Bool(root, "namesIncluded"), ids = Bool(root, "deviceIdsIncluded");
            if (names != fields.Contains("Names") || ids != fields.Contains("DeviceIds")) throw Invalid("Nesouhlasí vybraná pole.");
            var bootState = State(root, "bootIdentityState");
            var bootValue = Property(root, "bootIdentifier");
            BootIdentifier? boot = bootState == SampleState.Valid ? Boot(bootValue) : null;
            if (bootState != SampleState.Valid && bootValue.ValueKind != JsonValueKind.Null) throw Invalid("Neplatný stav boot identity.");
            var captured = Property(root, "capturedAtUtc");
            if (captured.ValueKind != JsonValueKind.String || !captured.TryGetDateTimeOffset(out var capturedAt)
                || capturedAt.Offset != TimeSpan.Zero) throw Invalid("Neplatný čas snímku.");
            var metadata = new SnapshotMetadata(capturedAt, Time(root, "capturedMonotonicNs"),
                Time(root, "processMeasuredAtMonotonicNs"), Time(root, "processMeasuredAtUnixNs", true),
                Time(root, "resourceMeasuredAtMonotonicNs"), State(root, "processStreamState"), State(root, "resourceStreamState"),
                bootState, boot, Bool(root, "paused"), Bool(root, "stale"), fields.ToFrozenSet(StringComparer.Ordinal));
            var rows = Array(root, "processes");
            if (UInt(root, "processesTotal") != rows.GetArrayLength()) throw Invalid("Nesouhlasí počet procesů.");
            var processes = new List<ImportedProcess>(rows.GetArrayLength());
            var keys = new HashSet<ProcessKey>();
            var pids = new HashSet<uint>();
            foreach (var value in rows.EnumerateArray()) {
                cancel.ThrowIfCancellationRequested();
                var process = ReadProcess(Object(value), metadata, names);
                if (!keys.Add(process.Key)) throw Invalid("Duplicitní životnost procesu.");
                if (!pids.Add(process.Key.Pid)) throw Invalid("Duplicitní PID v jednom snímku.");
                processes.Add(process);
            }
            ValidateSections(root, metadata, names, ids, cancel);
            return new(metadata, processes.AsReadOnly());
        } catch (JsonException error) { throw new InvalidDataException("Snímek obsahuje neplatný JSON.", error); }
    }

    public static Task<SnapshotComparisonResult> CompareAsync(ImportedSnapshot before, ImportedSnapshot after,
        CancellationToken cancel = default) => Task.Run(() => Compare(before, after, cancel), cancel);

    public static SnapshotComparisonResult Compare(ImportedSnapshot before, ImportedSnapshot after, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        var rows = new List<ProcessComparison>();
        var reason = !BootVerified(before.Metadata) || !BootVerified(after.Metadata) ? SnapshotComparisonReason.BootNotVerified
            : before.Metadata.BootIdentifier != after.Metadata.BootIdentifier ? SnapshotComparisonReason.BootChanged : SnapshotComparisonReason.None;
        if (reason != SnapshotComparisonReason.None) {
            foreach (var process in before.Processes) { cancel.ThrowIfCancellationRequested(); rows.Add(Single(process, true, reason)); }
            foreach (var process in after.Processes) { cancel.ThrowIfCancellationRequested(); rows.Add(Single(process, false, reason)); }
            return new(before, after, rows.AsReadOnly());
        }
        var remaining = after.Processes.ToDictionary(process => process.Key);
        var beforePids = PidKinds(before.Processes, cancel);
        var afterPids = PidKinds(after.Processes, cancel);
        bool complete = Complete(before.Metadata) && Complete(after.Metadata);
        foreach (var process in before.Processes) {
            cancel.ThrowIfCancellationRequested();
            if (process.IdentityVerified && remaining.TryGetValue(process.Key, out var other) && other.IdentityVerified) {
                remaining.Remove(process.Key);
                var metrics = CompareMetrics(process, other, cancel);
                bool changed = process.ParentPid != other.ParentPid
                    || (process.Name is not null && other.Name is not null && process.Name != other.Name) || metrics.Any(metric => metric.Changed);
                rows.Add(new(changed ? ProcessComparisonKind.Changed : ProcessComparisonKind.Unchanged,
                    SnapshotComparisonReason.None, process, other, metrics));
            } else rows.Add(Single(process, true, Unmatched(process, afterPids.GetValueOrDefault(process.Key.Pid), complete)));
        }
        foreach (var process in after.Processes) {
            cancel.ThrowIfCancellationRequested();
            if (remaining.ContainsKey(process.Key)) rows.Add(Single(process, false, Unmatched(process, beforePids.GetValueOrDefault(process.Key.Pid), complete)));
        }
        return new(before, after, rows.AsReadOnly());
    }

    private static bool BootVerified(SnapshotMetadata metadata) => metadata.BootIdentityState == SampleState.Valid
        && metadata.BootIdentifier is { IsEmpty: false };
    private static bool Complete(SnapshotMetadata metadata) => metadata.ProcessStreamState == SampleState.Valid && !metadata.Stale;

    private static Dictionary<uint, (bool Weak, uint Kinds)> PidKinds(IReadOnlyList<ImportedProcess> processes, CancellationToken cancel)
    {
        var result = new Dictionary<uint, (bool Weak, uint Kinds)>();
        foreach (var process in processes) {
            cancel.ThrowIfCancellationRequested();
            var prior = result.GetValueOrDefault(process.Key.Pid);
            result[process.Key.Pid] = (prior.Weak || !process.IdentityVerified, prior.Kinds | (1u << (int)process.Key.Kind));
        }
        return result;
    }

    private static SnapshotComparisonReason Unmatched(ImportedProcess process, (bool Weak, uint Kinds) opposite, bool complete)
    {
        if (!process.IdentityVerified || opposite.Weak) return SnapshotComparisonReason.WeakIdentity;
        if ((opposite.Kinds & ~(1u << (int)process.Key.Kind)) != 0) return SnapshotComparisonReason.GenerationKindChanged;
        return complete ? SnapshotComparisonReason.None : SnapshotComparisonReason.IncompleteProcessStream;
    }

    private static ProcessComparison Single(ImportedProcess process, bool before, SnapshotComparisonReason reason)
    {
        var metrics = process.Metrics.Select(pair => new ComparedMetric(pair.Key, before ? pair.Value : null,
            before ? null : pair.Value, null, null)).ToArray();
        return new(reason != SnapshotComparisonReason.None ? ProcessComparisonKind.NotComparable
            : before ? ProcessComparisonKind.Ended : ProcessComparisonKind.Created, reason,
            before ? process : null, before ? null : process, System.Array.AsReadOnly(metrics));
    }

    private static IReadOnlyList<ComparedMetric> CompareMetrics(ImportedProcess before, ImportedProcess after, CancellationToken cancel)
    {
        var result = new List<ComparedMetric>();
        foreach (string name in before.Metrics.Keys.Union(after.Metrics.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
            cancel.ThrowIfCancellationRequested();
            before.Metrics.TryGetValue(name, out var left); after.Metrics.TryGetValue(name, out var right);
            double? floating = null; decimal? integer = null;
            if (left is { State: SampleState.Valid } && right is { State: SampleState.Valid }
                && left.Unit == right.Unit && left.Source == right.Source) {
                if (left.UnsignedValue is ulong first && right.UnsignedValue is ulong second) integer = (decimal)second - first;
                if (left.Value is double a && right.Value is double b) floating = b - a;
            }
            result.Add(new(name, left, right, floating, integer));
        }
        return result.AsReadOnly();
    }

    private static ImportedProcess ReadProcess(JsonElement row, SnapshotMetadata metadata, bool names)
    {
        var key = new ProcessKey(Boot(Property(row, "bootIdentifier")), UInt(row, "pid"),
            EnumValue<GenerationKind>(Property(row, "generationKind")), UnsignedText(Property(row, "generationValue")));
        bool claim = Bool(row, "identityReliable");
        bool verified = claim && BootVerified(metadata) && key.Boot == metadata.BootIdentifier
            && !key.Boot.IsEmpty && key.Kind is GenerationKind.CreationTime or GenerationKind.SequenceNumber && key.Generation != 0;
        if (key.Kind == GenerationKind.Missing && key.Generation != 0) throw Invalid("Chybějící druh generace má nenulovou hodnotu.");
        string? name = names ? Text(row, "name") : null;
        if (!names) Absent(row, "name");
        uint parent = UInt(row, "parentPid"), threads = UInt(row, "threadCount"), handles = UInt(row, "handleCount");
        var metrics = new Dictionary<string, ImportedMetric>(StringComparer.Ordinal);
        var streamState = metadata.Stale ? SampleState.Stale : metadata.ProcessStreamState;
        metrics.Add("threadCount", Count(threads, streamState)); metrics.Add("handleCount", Count(handles, streamState));
        if (metadata.Fields.Contains("Cpu")) metrics.Add("cpu", Metric(Property(row, "cpu"), false, streamState)); else Absent(row, "cpu");
        if (metadata.Fields.Contains("Memory")) {
            var source = EnumValue<MetricSource>(Property(row, "memorySource"));
            metrics.Add("workingSet", Metric(Property(row, "workingSet"), true, streamState, source));
            metrics.Add("privateMemory", Metric(Property(row, "privateMemory"), true, streamState, source));
        } else { Absent(row, "memorySource"); Absent(row, "workingSet"); Absent(row, "privateMemory"); }
        if (metadata.Fields.Contains("Io")) {
            metrics.Add("readIo", Metric(Property(row, "readIo"), false, streamState));
            metrics.Add("writeIo", Metric(Property(row, "writeIo"), false, streamState));
        } else { Absent(row, "readIo"); Absent(row, "writeIo"); }
        return new(key, verified, name, parent, metrics.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static ImportedMetric Count(uint value, SampleState state) => new("count", MetricSource.NativeProcess, state,
        UnsignedValue: state == SampleState.Valid ? value : null);

    private static ImportedMetric Metric(JsonElement value, bool unsigned, SampleState streamState = SampleState.Valid, MetricSource? inheritedSource = null)
    {
        value = Object(value);
        string unit = Text(value, "unit");
        if (unit is not ("percent" or "percentOfMachine" or "bytes" or "bytesPerSecond" or "bitsPerSecond" or "degreesCelsius")
            || unsigned && unit != "bytes") throw Invalid("Neplatná jednotka metriky.");
        var source = inheritedSource ?? EnumValue<MetricSource>(Property(value, "source"));
        if (inheritedSource is not null && value.TryGetProperty("source", out _)) throw Invalid("Duplicitní zdroj metriky.");
        var state = State(value, "state");
        var number = Property(value, "value");
        double? floating = null; ulong? integer = null;
        if (state == SampleState.Valid) {
            if (number.ValueKind != JsonValueKind.Number) throw Invalid("Platná metrika nemá číselnou hodnotu.");
            if (unsigned) { if (!number.TryGetUInt64(out var raw)) throw Invalid("Neplatná celočíselná metrika."); integer = raw; }
            else { if (!number.TryGetDouble(out var raw) || !double.IsFinite(raw) || raw < 0) throw Invalid("Neplatná hodnota metriky."); floating = raw; }
        } else if (number.ValueKind != JsonValueKind.Null) throw Invalid("Neplatná metrika obsahuje hodnotu.");
        if (state == SampleState.Valid && streamState != SampleState.Valid) { state = streamState; floating = null; integer = null; }
        return new(unit, source, state, floating, integer);
    }

    private static void ValidateSections(JsonElement root, SnapshotMetadata metadata, bool names, bool ids, CancellationToken cancel)
    {
        if (metadata.Fields.Contains("Cpu")) {
            UInt(root, "logicalProcessorCount"); Metric(Property(root, "systemCpu"), false);
            var processors = new HashSet<(uint, uint)>();
            foreach (var row in Array(root, "logicalProcessors").EnumerateArray()) {
                cancel.ThrowIfCancellationRequested(); Object(row);
                if (!processors.Add((UInt(row, "group"), UInt(row, "index")))) throw Invalid("Duplicitní logický procesor.");
                Metric(Property(row, "cpu"), false);
            }
        } else { Absent(root, "logicalProcessorCount"); Absent(root, "systemCpu"); Absent(root, "logicalProcessors"); }
        if (metadata.Fields.Contains("Memory")) {
            var memory = Object(Property(root, "systemMemory"));
            var source = EnumValue<MetricSource>(Property(memory, "source"));
            foreach (string name in new[] { "physical", "available", "committed", "commitLimit", "cache", "pagedPool", "nonpagedPool", "pageFileCapacity", "pageFileUsed", "pageFilePeak" })
                Metric(Property(memory, name), true, inheritedSource: source);
        } else Absent(root, "systemMemory");
        if (!metadata.Fields.Contains("Devices")) { Absent(root, "devices"); Absent(root, "devicesTotal"); return; }
        var devices = Array(root, "devices");
        if (UInt(root, "devicesTotal") != devices.GetArrayLength()) throw Invalid("Nesouhlasí počet zařízení.");
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        uint index = 0;
        foreach (var row in devices.EnumerateArray()) {
            cancel.ThrowIfCancellationRequested(); Object(row);
            if (UInt(row, "index") != index++) throw Invalid("Neplatné pořadí zařízení.");
            var kind = EnumValue<ResourceKind>(Property(row, "kind"));
            if (kind is not (ResourceKind.Disk or ResourceKind.Network or ResourceKind.Gpu)) throw Invalid("Neplatný druh zařízení.");
            ValidateLabels(row, names, ids, identifiers);
            if (names) Text(row, "description"); else Absent(row, "description");
            Metric(Property(row, kind == ResourceKind.Network ? "receive" : "utilization"), false);
            if (kind == ResourceKind.Network) Metric(Property(row, "transmit"), false);
            if (kind == ResourceKind.Disk) { Metric(Property(row, "read"), false); Metric(Property(row, "write"), false); }
            if (kind != ResourceKind.Gpu) continue;
            Metric(Property(row, "dedicatedMemory"), false); Metric(Property(row, "sharedMemory"), false); Metric(Property(row, "temperature"), false);
            var engineIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var engine in Array(row, "engines").EnumerateArray()) {
                cancel.ThrowIfCancellationRequested(); Object(engine);
                if (Text(engine, "type") is not ("3D" or "Copy" or "VideoDecode" or "VideoEncode" or "Compute" or "VideoProcessing" or "Other")) throw Invalid("Neplatný druh GPU enginu.");
                ValidateLabels(engine, names, ids, engineIds); Metric(Property(engine, "utilization"), false);
            }
        }
    }

    private static void ValidateLabels(JsonElement row, bool names, bool ids, HashSet<string> identifiers)
    {
        if (names) Text(row, "name"); else Absent(row, "name");
        if (ids) { string id = Text(row, "id"); if (id.Length == 0 || !identifiers.Add(id)) throw Invalid("Prázdné nebo duplicitní ID zařízení."); }
        else Absent(row, "id");
    }

    private static void Preflight(ReadOnlySpan<byte> json, CancellationToken cancel)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = MaximumDepth });
        Span<bool> arrays = stackalloc bool[MaximumDepth + 1];
        Span<int> counts = stackalloc int[MaximumDepth + 1];
        Span<int> limits = stackalloc int[MaximumDepth + 1];
        Span<int> properties = stackalloc int[MaximumDepth + 1];
        arrays.Clear(); counts.Clear(); limits.Clear(); properties.Clear();
        int nextLimit = MaximumArrayItems, total = 0, tokens = 0;
        while (reader.Read()) {
            cancel.ThrowIfCancellationRequested();
            if (++tokens > MaximumTokens) throw Invalid("Snímek obsahuje příliš mnoho položek JSON.");
            int depth = reader.CurrentDepth;
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName) {
                if (reader.ValueSpan.Length > MaximumStringBytes) throw Invalid("Příliš dlouhý text ve snímku.");
                ValidateText(ref reader);
            }
            if (reader.TokenType == JsonTokenType.PropertyName) {
                if (++properties[depth - 1] > 64) throw Invalid("Příliš mnoho vlastností objektu.");
                nextLimit = reader.ValueTextEquals("processes"u8) ? MaximumProcesses : reader.ValueTextEquals("devices"u8) ? MaximumDevices
                    : reader.ValueTextEquals("logicalProcessors"u8) ? MaximumProcessors : reader.ValueTextEquals("engines"u8) ? MaximumEngines
                    : reader.ValueTextEquals("fields"u8) ? 6 : MaximumArrayItems;
            }
            if (depth > 0 && arrays[depth - 1] && reader.TokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject or JsonTokenType.PropertyName)) {
                if (++counts[depth - 1] > limits[depth - 1] || ++total > MaximumArrayItems) throw Invalid("Snímek obsahuje příliš mnoho řádků.");
            }
            if (reader.TokenType == JsonTokenType.StartArray) { arrays[depth] = true; counts[depth] = 0; limits[depth] = nextLimit; }
            if (reader.TokenType == JsonTokenType.EndArray) arrays[depth] = false;
            if (reader.TokenType == JsonTokenType.StartObject) properties[depth] = 0;
        }
    }

    private static void ValidateText(ref Utf8JsonReader reader)
    {
        char[]? rented = null;
        Span<char> decoded = reader.ValueSpan.Length <= 256 ? stackalloc char[256]
            : rented = ArrayPool<char>.Shared.Rent(reader.ValueSpan.Length);
        // The token kind is checked by the caller, so this exception identifies invalid Unicode.
        try { reader.CopyString(decoded); }
        catch (InvalidOperationException error) { throw new InvalidDataException("Snímek obsahuje neplatný Unicode.", error); }
        finally { if (rented is not null) ArrayPool<char>.Shared.Return(rented, clearArray: true); }
    }

    private static void ValidateProperties(JsonElement value, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object) {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) {
                if (!names.Add(property.Name)) throw Invalid("Duplicitní vlastnost JSON objektu.");
                ValidateProperties(property.Value, cancel);
            }
        } else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) ValidateProperties(item, cancel);
    }

    private static JsonElement Object(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value : throw Invalid("Očekáván objekt.");
    private static JsonElement Property(JsonElement value, string name) => value.TryGetProperty(name, out var result) ? result : throw Invalid("Chybí pole " + name + ".");
    private static JsonElement Array(JsonElement value, string name) { var result = Property(value, name); return result.ValueKind == JsonValueKind.Array ? result : throw Invalid("Očekáváno pole " + name + "."); }
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid("Očekáván text.");
    private static string Text(JsonElement value, string name) => Text(Property(value, name));
    private static bool Boolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Invalid("Očekávána pravdivostní hodnota.");
    private static bool Bool(JsonElement value, string name) => Boolean(Property(value, name));
    private static uint UInt(JsonElement value, string name) { var number = Property(value, name); return number.ValueKind == JsonValueKind.Number && number.TryGetUInt32(out uint result) ? result : throw Invalid("Neplatné celé číslo " + name + "."); }
    private static void Absent(JsonElement value, string name) { if (value.TryGetProperty(name, out _)) throw Invalid("Nevybrané pole " + name + " je přítomné."); }
    private static SampleState State(JsonElement value, string name) => EnumValue<SampleState>(Property(value, name));
    private static T EnumValue<T>(JsonElement value) where T : struct, Enum { string text = Text(value); return Enum.TryParse<T>(text, out var result) && Enum.IsDefined(result) && result.ToString() == text ? result : throw Invalid("Neznámý stav nebo druh."); }
    private static ulong UnsignedText(JsonElement value) { string text = Text(value); return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong result) ? result : throw Invalid("Neplatná generace procesu."); }
    private static long Time(JsonElement value, string name, bool signed = false) { string text = Text(value, name); return long.TryParse(text, signed ? NumberStyles.AllowLeadingSign : NumberStyles.None, CultureInfo.InvariantCulture, out long result) && (signed || result >= 0) ? result : throw Invalid("Neplatný čas " + name + "."); }
    private static BootIdentifier Boot(JsonElement value) { string text = Text(value); return text.Length == 32 && ulong.TryParse(text.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong high) && ulong.TryParse(text.AsSpan(16, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong low) ? new(low, high) : throw Invalid("Neplatná boot identita."); }
    private static InvalidDataException Invalid(string message) => new(message);
}
