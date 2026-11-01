using System.Globalization;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

[Flags]
internal enum SnapshotFields { Cpu = 1, Memory = 2, Io = 4, Devices = 8, Names = 16, DeviceIds = 32, Default = Cpu | Memory | Io | Devices }

internal static class SnapshotExport
{
    public static string Preview(SnapshotDocument snapshot, SnapshotFields fields)
    {
        using var stream = new MemoryStream();
        Write(stream, snapshot, fields, 3);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static Task<string?> SaveAsync(string path, SnapshotDocument snapshot, SnapshotFields fields, CancellationToken cancel = default) => Task.Run<string?>(() => {
        cancel.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        bool replace = File.Exists(path);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".nks-snapshot-" + Guid.NewGuid().ToString("N") + ".tmp");
        string backup = temporary + ".previous";
        try {
            using (var stream = CreateTemporary(path, temporary, replace)) {
                Write(stream, snapshot, fields, cancel: cancel);
                stream.Flush(true);
            }
            cancel.ThrowIfCancellationRequested();
            return Commit(temporary, path, backup, replace);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, cancel);

    internal static string? Commit(string temporary, string destination, string backup, bool replace)
    {
        if (!replace) { File.Move(temporary, destination); return null; }
        try { File.Replace(temporary, destination, backup, false); }
        catch (IOException) {
            // ReplaceFile error 1177 can leave the original at the backup path.
            if (!File.Exists(destination) && File.Exists(backup)) File.Move(backup, destination);
            throw;
        }
        return RemoveBackup(backup);
    }

    internal static string? RemoveBackup(string backup)
    {
        // Cleanup failure must not report a failed save after the new file is committed.
        try { File.Delete(backup); return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return backup; }
    }

    private static FileStream CreateTemporary(string destination, string temporary, bool replace)
    {
        if (!replace) return new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        bool encrypted = (File.GetAttributes(destination) & FileAttributes.Encrypted) != 0;
        var permissions = new FileInfo(destination).GetAccessControl(AccessControlSections.Access);
        var stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, permissions);
        if (!encrypted) return stream;
        stream.Dispose();
        File.Encrypt(temporary);
        return new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    internal static void Write(Stream stream, SnapshotDocument snapshot, SnapshotFields fields, int limit = int.MaxValue, CancellationToken cancel = default)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        bool includeNames = fields.HasFlag(SnapshotFields.Names);
        writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1); writer.WriteString("application", "NKS Task Manager");
        writer.WriteString("capturedAtUtc", snapshot.CapturedAt.ToUniversalTime());
        writer.WriteString("capturedMonotonicNs", snapshot.CapturedMonotonicNs.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("processMeasuredAtMonotonicNs", snapshot.ProcessTimeNs.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("processMeasuredAtUnixNs", snapshot.ProcessWallTimeUnixNs.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("resourceMeasuredAtMonotonicNs", snapshot.ResourceTimeNs.ToString(CultureInfo.InvariantCulture));
        SampleState StreamState(long measured, bool error) => error ? SampleState.Error : measured <= 0 ? SampleState.First
            : !snapshot.Paused && snapshot.CapturedMonotonicNs - measured > MonitorModel.StaleAfterNs ? SampleState.Stale : SampleState.Valid;
        writer.WriteString("processStreamState", StreamState(snapshot.ProcessTimeNs, snapshot.ProcessError).ToString());
        writer.WriteString("resourceStreamState", StreamState(snapshot.ResourceTimeNs, snapshot.ResourceError).ToString());
        writer.WriteBoolean("paused", snapshot.Paused); writer.WriteBoolean("stale", snapshot.Stale);
        writer.WriteString("bootIdentityState", snapshot.Boot.State.ToString());
        writer.WriteString("bootIdentifier", snapshot.Boot.State == SampleState.Valid ? BootId(snapshot.Boot.Identifier) : null);
        writer.WriteBoolean("namesIncluded", includeNames); writer.WriteBoolean("deviceIdsIncluded", fields.HasFlag(SnapshotFields.DeviceIds));
        writer.WriteStartArray("fields");
        foreach (var field in new[] { SnapshotFields.Cpu, SnapshotFields.Memory, SnapshotFields.Io, SnapshotFields.Devices, SnapshotFields.Names, SnapshotFields.DeviceIds })
            if (fields.HasFlag(field)) writer.WriteStringValue(field.ToString());
        writer.WriteEndArray();
        writer.WriteNumber("processesTotal", snapshot.Processes.Length);
        if (limit != int.MaxValue) writer.WriteBoolean("previewOnly", true);
        if (fields.HasFlag(SnapshotFields.Cpu)) {
            writer.WriteNumber("logicalProcessorCount", snapshot.LogicalProcessorCount);
            MetricValue(writer, "systemCpu", snapshot.Cpu, "percentOfMachine", snapshot.Stale);
            writer.WriteStartArray("logicalProcessors");
            foreach (var processor in snapshot.Processors.Take(limit)) {
                writer.WriteStartObject(); writer.WriteNumber("group", processor.Group); writer.WriteNumber("index", processor.Index);
                MetricValue(writer, "cpu", processor.Utilization, "percent", snapshot.Stale); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (fields.HasFlag(SnapshotFields.Memory)) WriteMemory(writer, snapshot);
        writer.WriteStartArray("processes");
        foreach (var process in snapshot.Processes.Take(limit)) {
            cancel.ThrowIfCancellationRequested();
            bool stale = snapshot.Stale || process.Stale;
            writer.WriteStartObject(); writer.WriteNumber("pid", process.Key.Pid);
            writer.WriteString("bootIdentifier", BootId(process.Key.Boot));
            writer.WriteString("generationKind", process.Key.Kind.ToString());
            writer.WriteString("generationValue", process.Key.Generation.ToString(CultureInfo.InvariantCulture));
            writer.WriteBoolean("identityReliable", snapshot.Boot.State == SampleState.Valid && process.Key.Boot == snapshot.Boot.Identifier && !process.Key.Boot.IsEmpty
                && process.Key.Kind != GenerationKind.Missing && process.Key.Generation != 0);
            if (includeNames) writer.WriteString("name", process.Name);
            writer.WriteNumber("parentPid", process.ParentPid); writer.WriteNumber("threadCount", process.Threads); writer.WriteNumber("handleCount", process.Handles);
            if (fields.HasFlag(SnapshotFields.Cpu)) MetricValue(writer, "cpu", process.Cpu, "percentOfMachine", stale);
            if (fields.HasFlag(SnapshotFields.Memory)) {
                writer.WriteString("memorySource", MetricSource.NativeProcess.ToString());
                UnsignedValue(writer, "workingSet", process.WorkingBytes, stale ? SampleState.Stale : SampleState.Valid);
                UnsignedValue(writer, "privateMemory", process.PrivateBytes, stale ? SampleState.Stale : SampleState.Valid);
            }
            if (fields.HasFlag(SnapshotFields.Io)) {
                MetricValue(writer, "readIo", process.ReadIo, "bytesPerSecond", stale);
                MetricValue(writer, "writeIo", process.WriteIo, "bytesPerSecond", stale);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (fields.HasFlag(SnapshotFields.Devices)) {
            writer.WriteNumber("devicesTotal", snapshot.Resources.Length); writer.WriteStartArray("devices");
            int ordinal = 0;
            foreach (var resource in snapshot.Resources.Take(limit)) {
                cancel.ThrowIfCancellationRequested();
                writer.WriteStartObject(); writer.WriteNumber("index", ordinal++); writer.WriteString("kind", resource.Kind.ToString());
                if (includeNames) { writer.WriteString("name", resource.Title); writer.WriteString("description", resource.Description); }
                if (fields.HasFlag(SnapshotFields.DeviceIds)) writer.WriteString("id", resource.Id);
                string unit = resource.Kind == ResourceKind.Network ? "bitsPerSecond" : "percent";
                MetricValue(writer, resource.Kind == ResourceKind.Network ? "receive" : "utilization", resource.Primary, unit, resource.Stale);
                if (resource.Kind == ResourceKind.Network) MetricValue(writer, "transmit", resource.Secondary, unit, resource.Stale);
                if (resource.Kind == ResourceKind.Disk) {
                    MetricValue(writer, "read", resource.Secondary, "bytesPerSecond", resource.Stale);
                    MetricValue(writer, "write", resource.Tertiary, "bytesPerSecond", resource.Stale);
                }
                if (resource.Kind == ResourceKind.Gpu) {
                    MetricValue(writer, "dedicatedMemory", resource.Secondary, "bytes", resource.Stale);
                    MetricValue(writer, "sharedMemory", resource.Tertiary, "bytes", resource.Stale);
                    MetricValue(writer, "temperature", resource.Temperature, "degreesCelsius", resource.Stale);
                    writer.WriteStartArray("engines");
                    foreach (var engine in resource.Engines.Take(limit)) {
                        writer.WriteStartObject();
                        writer.WriteString("type", engine.Name is "3D" or "Copy" or "VideoDecode" or "VideoEncode" or "Compute" or "VideoProcessing" ? engine.Name : "Other");
                        if (includeNames) writer.WriteString("name", engine.Name);
                        if (fields.HasFlag(SnapshotFields.DeviceIds)) writer.WriteString("id", engine.Id);
                        MetricValue(writer, "utilization", engine.Utilization, "percent", resource.Stale); writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static string BootId(BootIdentifier boot) => boot.High.ToString("x16", CultureInfo.InvariantCulture) + boot.Low.ToString("x16", CultureInfo.InvariantCulture);

    private static void MetricValue(Utf8JsonWriter writer, string name, Metric metric, string unit, bool stale)
    {
        var state = stale && metric.State == SampleState.Valid ? SampleState.Stale : metric.State;
        if (state == SampleState.Valid && (!double.IsFinite(metric.Value) || metric.Value < 0)) state = SampleState.Error;
        writer.WriteStartObject(name); writer.WriteString("unit", unit); writer.WriteString("source", metric.Source.ToString()); writer.WriteString("state", state.ToString());
        if (state == SampleState.Valid) writer.WriteNumber("value", metric.Value); else writer.WriteNull("value");
        writer.WriteEndObject();
    }

    private static void UnsignedValue(Utf8JsonWriter writer, string name, ulong value, SampleState state)
    {
        writer.WriteStartObject(name); writer.WriteString("unit", "bytes"); writer.WriteString("state", state.ToString());
        if (state == SampleState.Valid) writer.WriteNumber("value", value); else writer.WriteNull("value");
        writer.WriteEndObject();
    }

    private static void WriteMemory(Utf8JsonWriter writer, SnapshotDocument snapshot)
    {
        var memory = snapshot.Memory;
        var state = snapshot.Stale && memory.State == SampleState.Valid ? SampleState.Stale : memory.State;
        writer.WriteStartObject("systemMemory"); writer.WriteString("source", MetricSource.PerformanceInfo.ToString());
        UnsignedValue(writer, "physical", memory.PhysicalBytes, state); UnsignedValue(writer, "available", memory.AvailableBytes, state);
        UnsignedValue(writer, "committed", memory.CommittedBytes, state); UnsignedValue(writer, "commitLimit", memory.CommitLimitBytes, state);
        UnsignedValue(writer, "cache", memory.CacheBytes, state); UnsignedValue(writer, "pagedPool", memory.PagedPoolBytes, state);
        UnsignedValue(writer, "nonpagedPool", memory.NonpagedPoolBytes, state);
        state = snapshot.Stale && memory.PageFileState == SampleState.Valid ? SampleState.Stale : memory.PageFileState;
        UnsignedValue(writer, "pageFileCapacity", memory.PageFileBytes, state); UnsignedValue(writer, "pageFileUsed", memory.PageFileUsedBytes, state);
        UnsignedValue(writer, "pageFilePeak", memory.PageFilePeakBytes, state); writer.WriteEndObject();
    }
}
