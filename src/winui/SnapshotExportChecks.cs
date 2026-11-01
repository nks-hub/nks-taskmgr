using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    internal static Task SnapshotExportChecks(SnapshotDocument live) => Task.Run(async () => {
        const string secret = "EXPORT_PRIVATE_TEST_VALUE";
        var boot = new BootIdentifier(1, 2);
        var valid = new Metric { State = SampleState.Valid, Source = MetricSource.NativeProcess };
        var denied = new Metric { State = SampleState.AccessDenied, Source = MetricSource.NativeProcess };
        var unsupported = new Metric { State = SampleState.Unsupported, Source = MetricSource.PerformanceCounter };
        var snapshot = new SnapshotDocument {
            CapturedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), CapturedMonotonicNs = 1234,
            ProcessTimeNs = 1000, ProcessWallTimeUnixNs = 9000, ResourceTimeNs = 1100, LogicalProcessorCount = 4,
            Boot = new BootIdentityInfo { Low = 1, High = 2, State = SampleState.Valid }, Cpu = valid,
            Memory = new MemoryInfo { State = SampleState.Valid, PhysicalBytes = 16_000_000_000, AvailableBytes = 8_000_000_000, PageFileState = SampleState.Unavailable },
            Processes = [new(new(boot, 42, GenerationKind.CreationTime, ulong.MaxValue - 1), secret, 4, 3, 6, 64, 128, valid, denied, new(), false)],
            Resources = [new(secret, ResourceKind.Gpu, secret, secret, unsupported, valid, valid, unsupported, false, [new(secret, secret, unsupported)])]
        };
        string Json(SnapshotDocument value, SnapshotFields fields = SnapshotFields.Default) {
            using var stream = new MemoryStream(); SnapshotExport.Write(stream, value, fields); return Encoding.UTF8.GetString(stream.ToArray());
        }
        string json = Json(snapshot);
        Require(!json.Contains(secret, StringComparison.Ordinal), "Default snapshot leaked names or device identifiers.");
        using (var parsed = JsonDocument.Parse(json)) {
            var process = parsed.RootElement.GetProperty("processes")[0];
            Require(process.GetProperty("cpu").GetProperty("value").GetDouble() == 0, "Measured zero disappeared from the snapshot.");
            Require(process.GetProperty("workingSet").GetProperty("value").GetUInt64() == 64
                && process.GetProperty("privateMemory").GetProperty("value").GetUInt64() == 128, "Working set and private memory were confused in export.");
            Require(process.GetProperty("readIo").GetProperty("state").GetString() == "AccessDenied"
                && process.GetProperty("readIo").GetProperty("value").ValueKind == JsonValueKind.Null, "Access denial became measured zero in export.");
            Require(process.GetProperty("generationValue").GetString() == (ulong.MaxValue - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                && process.GetProperty("identityReliable").GetBoolean(), "Export rounded or lost the process lifetime key.");
            Require(parsed.RootElement.GetProperty("devices")[0].GetProperty("utilization").GetProperty("value").ValueKind == JsonValueKind.Null,
                "Unsupported device data became a valid value.");
            Require(!parsed.RootElement.TryGetProperty("previewOnly", out _), "The full export was marked as a preview.");
        }
        using (var named = JsonDocument.Parse(Json(snapshot, SnapshotFields.Default | SnapshotFields.Names))) {
            Require(named.RootElement.GetProperty("processes")[0].GetProperty("name").GetString() == secret, "Explicitly selected process names were omitted.");
            Require(!named.RootElement.GetProperty("devices")[0].TryGetProperty("id", out _), "Selecting names also exposed device identifiers.");
        }
        using (var identityOnly = JsonDocument.Parse(Json(snapshot, 0))) {
            var process = identityOnly.RootElement.GetProperty("processes")[0];
            Require(!process.TryGetProperty("cpu", out _) && !process.TryGetProperty("workingSet", out _)
                && !process.TryGetProperty("readIo", out _) && !identityOnly.RootElement.TryGetProperty("devices", out _), "Unselected fields were exported.");
        }
        using (var stale = JsonDocument.Parse(Json(snapshot with { Stale = true }))) {
            Require(stale.RootElement.GetProperty("processes")[0].GetProperty("cpu").GetProperty("value").ValueKind == JsonValueKind.Null,
                "Stale process data was exported as current.");
        }
        using (var missing = JsonDocument.Parse(Json(snapshot with { ResourceTimeNs = 0, Resources = [] })))
            Require(missing.RootElement.GetProperty("resourceStreamState").GetString() == "First", "An uninitialized device stream appeared as an empty measured inventory.");
        using (var changedClock = JsonDocument.Parse(Json(snapshot with { CapturedAt = snapshot.CapturedAt.AddDays(-1), ProcessWallTimeUnixNs = 1 })))
        using (var original = JsonDocument.Parse(json)) {
            var before = original.RootElement.GetProperty("processes")[0]; var after = changedClock.RootElement.GetProperty("processes")[0];
            Require(before.GetProperty("bootIdentifier").GetString() == after.GetProperty("bootIdentifier").GetString()
                && before.GetProperty("generationValue").GetString() == after.GetProperty("generationValue").GetString(), "A wall-clock change altered exported process identity.");
        }
        Require(live.Processes.Length > 0 && live.ProcessTimeNs > 0, "The live snapshot had no process stream.");
        string directory = Path.Combine(Path.GetTempPath(), "nks-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "snapshot.json"), blocked = Path.Combine(directory, "existing-directory"), race = Path.Combine(directory, "race.json"), backup = Path.Combine(directory, "retained-backup.json");
        try {
            await SnapshotExport.SaveAsync(file, live, SnapshotFields.Default);
            using (var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(file)))
                Require(saved.RootElement.GetProperty("processes").GetArrayLength() == live.Processes.Length, "Saving dropped live processes.");
            await File.WriteAllTextAsync(file, "Keep this file");
            var permissions = new FileSecurity(); permissions.SetAccessRuleProtection(true, false);
            using (var identity = WindowsIdentity.GetCurrent())
                permissions.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(permissions);
            string originalPermissions = new FileInfo(file).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await SnapshotExport.SaveAsync(file, snapshot, SnapshotFields.Default, cancellation.Token); throw new InvalidOperationException("Cancelled export succeeded."); }
            catch (OperationCanceledException) { }
            Require(await File.ReadAllTextAsync(file) == "Keep this file", "Cancelled export changed an existing file.");
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                try { await SnapshotExport.SaveAsync(file, snapshot, SnapshotFields.Default); throw new InvalidOperationException("Export replaced a locked file."); }
                catch (IOException) { }
            }
            Require(await File.ReadAllTextAsync(file) == "Keep this file", "Failed replacement changed the original file.");
            Directory.CreateDirectory(blocked);
            try { await SnapshotExport.SaveAsync(blocked, snapshot, SnapshotFields.Default); throw new InvalidOperationException("Export overwrote a directory."); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            Require(Directory.Exists(blocked) && Directory.GetFiles(directory, ".nks-snapshot-*.tmp").Length == 0, "Failed export left temporary files or changed a directory.");
            await File.WriteAllTextAsync(race, "A file created by another writer");
            await File.WriteAllTextAsync(backup, "New snapshot");
            try { SnapshotExport.Commit(backup, race, file + ".previous", false); throw new InvalidOperationException("A newly created target was overwritten."); }
            catch (IOException) { }
            Require(await File.ReadAllTextAsync(race) == "A file created by another writer", "The new-file path replaced another writer's content.");
            using (var lockedBackup = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
                Require(SnapshotExport.RemoveBackup(backup) == backup, "Cleanup failure was confused with a failed save.");
            Require(File.Exists(backup), "A retained backup disappeared.");
            Require(SnapshotExport.RemoveBackup(backup) is null && !File.Exists(backup), "Unlocked backup cleanup failed.");
            await SnapshotExport.SaveAsync(file, snapshot, SnapshotFields.Default);
            Require(!File.ReadAllText(file).Contains(secret, StringComparison.Ordinal), "Saved default snapshot leaked the private test value.");
            Require(new FileInfo(file).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access) == originalPermissions,
                "Replacing a snapshot changed its access rules.");
            Require(Directory.GetFiles(directory, ".nks-snapshot-*").Length == 0, "Completed export left temporary files or backups.");
        } finally {
            if (File.Exists(file)) File.Delete(file);
            if (File.Exists(race)) File.Delete(race);
            if (File.Exists(backup)) File.Delete(backup);
            if (Directory.Exists(blocked)) Directory.Delete(blocked);
            Directory.Delete(directory);
        }
    });
}
