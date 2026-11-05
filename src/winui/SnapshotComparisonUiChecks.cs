using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;
using Windows.Graphics;

namespace NksTaskMgr.WinUI;

internal static class SnapshotComparisonUiChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static void CommandAvailable(Button command, long measured) =>
        Require(measured == 0 && command.IsEnabled && command.Width == 35, "Snapshot comparison requires live data or lost its toolbar size.");
    internal static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nks-comparison-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string before = Path.Combine(directory, "before.json"), after = Path.Combine(directory, "after.json"), invalid = Path.Combine(directory, "invalid.json"), unicode = Path.Combine(directory, "unicode.json");
        SnapshotComparisonView? window = null;
        try {
            await Task.Run(async () => {
                var boot = new BootIdentifier(1, 2);
                var valid = new Metric { State = SampleState.Valid, Source = MetricSource.NativeProcess };
                var snapshot = new SnapshotDocument {
                    CapturedAt = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero), CapturedMonotonicNs = 2000, ProcessTimeNs = 1000,
                    Boot = new BootIdentityInfo { Low = 1, High = 2, State = SampleState.Valid }, Cpu = valid, LogicalProcessorCount = 1,
                    Memory = new MemoryInfo { State = SampleState.Valid, PageFileState = SampleState.Unavailable },
                    Processes = Enumerable.Range(1, 10_000).Select(pid => new SnapshotProcess(new(boot, (uint)pid, GenerationKind.CreationTime, (ulong)pid),
                        pid == 1 ? new string('x', 119) + "\U0001F642" : "Historický proces " + pid, 0, 1, 2, 64, 128, valid,
                        new Metric { State = SampleState.AccessDenied, Source = MetricSource.NativeProcess }, valid, false)).ToArray()
                };
                const SnapshotFields fields = SnapshotFields.Cpu | SnapshotFields.Memory | SnapshotFields.Io | SnapshotFields.Names;
                await SnapshotExport.SaveAsync(before, snapshot, fields);
                await SnapshotExport.SaveAsync(after, snapshot with { CapturedAt = snapshot.CapturedAt.AddHours(-1) }, fields);
                await File.WriteAllTextAsync(invalid, "{");
                await File.WriteAllTextAsync(unicode, "{\"\\uD800\":0}");
            });
            window = new SnapshotComparisonView(); window.Activate();
            await ComparisonRendered();
            var canceled = window.CompareFilesAsync(before, after);
            await ComparisonRendered(1);
            Require(window.IsBusy, "The cancellation check did not observe a running snapshot import after a rendered frame.");
            window.CancelComparison(); await canceled;
            Require(!window.IsBusy && window.ResultsList.Items.Count == 0 && window.StatusText.Contains("zrušeno", StringComparison.Ordinal),
                "Cancelling the production comparison path left partial results or a busy window.");
            var heartbeat = new TaskCompletionSource<bool>();
            window.DispatcherQueue.TryEnqueue(() => heartbeat.TrySetResult(window.IsBusy));
            var imported = window.CompareFilesAsync(before, after);
            Require(await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5)), "Snapshot import blocked the UI queue.");
            await imported; await ComparisonRendered();
            Require(window.ResultsList.Items.Count == 10_000 && window.StatusText == "Porovnání dokončeno.", "The production import path lost snapshot rows.");
            Require(!((string)window.ResultsList.Items[0]).EndsWith("\ud83d…", StringComparison.Ordinal), "A comparison label split a Unicode scalar.");
            Require(window.SummaryText.IndexOf("12:00:00.000 UTC", StringComparison.Ordinal) < window.SummaryText.IndexOf("11:00:00.000 UTC", StringComparison.Ordinal),
                "The UI reversed snapshots according to their wall-clock times.");
            window.StatusFilter.SelectedIndex = 4;
            Require(window.ResultsList.Items.Count == 10_000, "The unchanged filter lost imported rows.");
            int realized = Enumerable.Range(0, 10_000).Count(index => window.ResultsList.ContainerFromIndex(index) is not null);
            Require(realized > 0 && realized < 100, "Snapshot comparison materialized its complete 10,000-row list.");
            window.ResultsList.SelectedIndex = 9999; window.ResultsList.ScrollIntoView(window.ResultsList.Items[9999]);
            await ComparisonRendered();
            Require(window.ResultsList.ContainerFromIndex(9999) is not null && window.DetailText.Contains("Historický proces 10000", StringComparison.Ordinal),
                "The virtualized comparison could not select its final historical row.");
            window.StatusFilter.SelectedIndex = 0;
            Require(window.ResultsList.Items.Count == 10_000, "The all-status filter lost imported rows.");
            window.StatusFilter.SelectedIndex = 3;
            Require(window.ResultsList.Items.Count == 0 && window.DetailText == "Vyberte proces ze seznamu.", "An empty filter kept the previous detail.");
            window.StatusFilter.SelectedIndex = 0;
            window.AppWindow.ResizeDip(720, 500); await ComparisonRendered(); window.Root.UpdateLayout();
            foreach (var button in window.Commands) {
                var bounds = button.TransformToVisual(window.Root).TransformBounds(new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
                Require(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.Root.ActualWidth + 1 && bounds.Bottom <= window.Root.ActualHeight + 1,
                    "A comparison command is clipped in a 720x500 window.");
            }
            Require(window.ResultsList.ActualHeight > 20, "Small comparison windows lost the scrollable result list.");
            var denied = new ImportedMetric("bytesPerSecond", MetricSource.NativeProcess, SampleState.AccessDenied);
            Require(SnapshotComparisonView.MetricText(denied).Contains("Chybí oprávnění", StringComparison.Ordinal)
                && !SnapshotComparisonView.MetricText(denied).Contains("0 B/s", StringComparison.Ordinal), "Unavailable imported data became measured zero in the UI.");
            Require(SnapshotComparisonView.DeltaText(new ComparedMetric("readIo", denied, denied, null, null)) == "Nelze určit.",
                "The UI invented an unavailable difference.");
            var original = await SnapshotComparison.ReadAsync(before);
            var otherBoot = new BootIdentifier(3, 4);
            var different = original with { Metadata = original.Metadata with { BootIdentifier = otherBoot },
                Processes = original.Processes.Take(1).Select(process => process with { Key = process.Key with { Boot = otherBoot } }).ToArray() };
            var single = original with { Processes = original.Processes.Take(1).ToArray() };
            window.ShowResult(await SnapshotComparison.CompareAsync(single, different));
            Require(window.SummaryText.Contains("různých spuštění systému", StringComparison.Ordinal)
                && window.DetailText.Contains("různých spuštění systému", StringComparison.Ordinal), "The UI hid why historical processes cannot be compared.");
            await window.CompareFilesAsync(invalid, after);
            Require(window.ResultsList.Items.Count == 0 && !window.IsBusy && window.StatusText.StartsWith("Původní snímek:", StringComparison.Ordinal),
                "An import failure retained old results or lost the original file role.");
            await window.CompareFilesAsync(unicode, after);
            Require(window.ResultsList.Items.Count == 0 && !window.IsBusy && window.StatusText.StartsWith("Původní snímek:", StringComparison.Ordinal),
                "Invalid Unicode escaped the comparison import error boundary.");
            await window.CompareFilesAsync(before, after);
            Require(window.ResultsList.Items.Count == 10_000 && !window.IsBusy && window.StatusText == "Porovnání dokončeno.",
                "The comparison window did not recover after an invalid snapshot.");
            App.Trace($"Snapshot comparison UI passed: production import and cancel; responsive queue; {realized}/10000 realized rows; status filters; reversed UTC; read-only detail; unavailable metrics; 720x500; invalid JSON/Unicode and retry");
        } finally {
            window?.Close();
            foreach (string file in new[] { before, after, invalid, unicode }) if (File.Exists(file)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static async Task ComparisonRendered(int requiredFrames = 2)
    {
        var ready = new TaskCompletionSource();
        int frames = 0;
        void Rendered(object? sender, object args) { if (++frames == requiredFrames) ready.TrySetResult(); }
        CompositionTarget.Rendering += Rendered;
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= Rendered; }
    }
}
