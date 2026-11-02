using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private bool sortBenchmarkRunning;
    private double sortModelMs, sortHeaderMs, sortOverviewMs;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out ulong created, out ulong exited, out ulong kernel, out ulong user);

    private static ulong UiThreadCpu100Ns()
    {
        if (!GetThreadTimes(new nint(-2), out _, out _, out ulong kernel, out ulong user))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        return kernel + user;
    }

    private void SortProcesses(int column)
    {
        long start = Stopwatch.GetTimestamp();
        model.Sort(column);
        long sorted = Stopwatch.GetTimestamp();
        processTable.SetSort(model.SortColumn, model.SortDescending);
        long header = Stopwatch.GetTimestamp();
        RefreshProcesses();
        sortModelMs = Stopwatch.GetElapsedTime(start, sorted).TotalMilliseconds;
        sortHeaderMs = Stopwatch.GetElapsedTime(sorted, header).TotalMilliseconds;
        sortOverviewMs = Stopwatch.GetElapsedTime(header).TotalMilliseconds;
    }

    private static async Task NextRenderedFrame()
    {
        var ready = new TaskCompletionSource();
        void Rendered(object? sender, object args) => ready.TrySetResult();
        CompositionTarget.Rendering += Rendered;
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { CompositionTarget.Rendering -= Rendered; }
    }

    private static unsafe IEnumerable<ProcessRow> ReplayRows(BootIdentifier boot)
    {
        var rows = new List<ProcessRow>(10_000);
        for (uint i = 1; i <= 10_000; ++i) {
            string name = "Process " + (i * 37 % 997).ToString("D3", CultureInfo.InvariantCulture);
            var row = new ProcessRow(new ProcessKey(boot, i, GenerationKind.CreationTime, i));
            fixed (char* text = name) row.Update(new ProcessInfo {
                Pid = row.Key.Pid, Generation = row.Key.Generation, GenerationKind = row.Key.Kind,
                Name = new String16 { Data = text, Length = (uint)name.Length }, WorkingSetBytes = i * 7919 % 1_000_000,
                PrivateBytes = i * 3571 % 1_000_000, CpuPercent = new Metric { State = SampleState.Valid, Value = i * 31 % 100 },
                ReadIoBytesPerSecond = new Metric { State = SampleState.Valid, Value = i * 101 % 1000 },
                WriteIoBytesPerSecond = new Metric { State = SampleState.Valid, Value = i * 137 % 1000 }
            });
            row.SetCreatedTime(row.Key.Generation);
            rows.Add(row);
        }
        return rows;
    }

    private async void RunSortBenchmark()
    {
        sortBenchmarkRunning = true;
        bool iconsEnabled = !Environment.GetCommandLineArgs().Contains("--bench-sort-no-icons", StringComparer.Ordinal);
        ProcessIconView.SuppressForBenchmark = !iconsEnabled;
        string path = App.SortBenchmarkPath!;
        try {
            if (model.BootIdentity.State != SampleState.Valid || model.BootIdentity.Identifier.IsEmpty)
                throw new InvalidOperationException("Sort benchmark requires a verified boot identity.");
            model.SetPaused(true); model.SetMode(ProcessMode.All); model.SetQuery(""); ShowView(false);
            using var output = new StreamWriter(path, false);
            await output.WriteLineAsync("scenario,rows,column,descending,handler_ms,layout_ms,two_frames_ms,icon_decodes_started,ui_cpu_ms,ui_alloc_bytes,gen0_gc,gen1_gc,gen2_gc,sort_model_ms,sort_header_ms,sort_overview_ms,slots_assign_ms,slots_restore_ms,slots_focus_ms,icons_enabled,icon_apply_ms,icon_apply_max_ms,icon_start_ms,icon_start_max_ms");
            foreach (string scenario in new[] { "live", "replay" }) {
                if (scenario == "replay") model.ReplayForBenchmark(ReplayRows(model.BootIdentity.Identifier));
                SortProcesses(0); processList.UpdateLayout(); await NextRenderedFrame(); await NextRenderedFrame();
                var selected = model.VisibleProcesses.Skip(model.VisibleProcesses.Count / 2).Take(2).ToArray();
                foreach (var row in selected) processList.SelectedItems.Add(slotsByRow[row]);
                var primary = SelectedProcess;
                for (int round = 0; round < 2; ++round) for (int column = 0; column < 7; ++column) for (int direction = 0; direction < 2; ++direction) {
                    var container = processList.ContainerFromIndex(0) as ListViewItem;
                    var visual = container is null ? null : Descendant<ProcessRowGrid>(container);
                    int decodedBefore = ProcessIconView.DecodedImagesCreated;
                    double applyBefore = ProcessIconView.ApplyMs, startBefore = ProcessIconView.DecodeStartMs;
                    ProcessIconView.BeginProfileSample();
                    int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    ulong cpuBefore = UiThreadCpu100Ns();
                    var watch = Stopwatch.StartNew();
                    SortProcesses(column); double handler = watch.Elapsed.TotalMilliseconds;
                    processList.UpdateLayout(); double layout = watch.Elapsed.TotalMilliseconds;
                    double uiCpu = (UiThreadCpu100Ns() - cpuBefore) / 10_000d;
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                    int gen0Collections = GC.CollectionCount(0) - gen0, gen1Collections = GC.CollectionCount(1) - gen1, gen2Collections = GC.CollectionCount(2) - gen2;
                    if (container is not null && (!ReferenceEquals(container, processList.ContainerFromIndex(0))
                        || !ReferenceEquals(visual, Descendant<ProcessRowGrid>(container)))) throw new InvalidOperationException("Sorting recreated a visible process row.");
                    await NextRenderedFrame(); await NextRenderedFrame(); watch.Stop();
                    if (selected.Any(row => !processList.SelectedItems.Contains(slotsByRow[row]))) throw new InvalidOperationException("Sorting lost a selected process identity.");
                    if (SelectedProcess != primary) throw new InvalidOperationException("Sorting changed the primary selected process.");
                    await output.WriteLineAsync(string.Join(',', scenario, model.VisibleProcesses.Count, column, model.SortDescending,
                        handler.ToString("F3", CultureInfo.InvariantCulture), layout.ToString("F3", CultureInfo.InvariantCulture), watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                        ProcessIconView.DecodedImagesCreated - decodedBefore, uiCpu.ToString("F3", CultureInfo.InvariantCulture), allocated,
                        gen0Collections, gen1Collections, gen2Collections, sortModelMs.ToString("F3", CultureInfo.InvariantCulture),
                        sortHeaderMs.ToString("F3", CultureInfo.InvariantCulture), sortOverviewMs.ToString("F3", CultureInfo.InvariantCulture),
                        slotsAssignMs.ToString("F3", CultureInfo.InvariantCulture), slotsRestoreMs.ToString("F3", CultureInfo.InvariantCulture), slotsFocusMs.ToString("F3", CultureInfo.InvariantCulture), iconsEnabled,
                        (ProcessIconView.ApplyMs - applyBefore).ToString("F3", CultureInfo.InvariantCulture), ProcessIconView.ApplyMaxMs.ToString("F3", CultureInfo.InvariantCulture),
                        (ProcessIconView.DecodeStartMs - startBefore).ToString("F3", CultureInfo.InvariantCulture), ProcessIconView.DecodeStartMaxMs.ToString("F3", CultureInfo.InvariantCulture)));
                    await output.FlushAsync();
                }
                processList.ScrollIntoView(slotsByRow[selected[0]], ScrollIntoViewAlignment.Leading);
                await NextRenderedFrame(); await NextRenderedFrame();
                var scroll = Descendant<ScrollViewer>(processList)!;
                var anchor = model.VisibleProcesses[Math.Min(model.VisibleProcesses.Count - 1, (int)(scroll.VerticalOffset / ProcessTable.RowHeight))];
                SortProcesses(1); processList.UpdateLayout(); await NextRenderedFrame(); await NextRenderedFrame();
                if (model.VisibleProcesses[Math.Min(model.VisibleProcesses.Count - 1, (int)(scroll.VerticalOffset / ProcessTable.RowHeight))] != anchor)
                    throw new InvalidOperationException("Sorting lost the scrolled process identity.");
            }
        } catch (Exception error) {
            File.AppendAllText(path + ".error.txt", error.ToString()); Environment.ExitCode = 1;
        } finally { RequestMainClose(); }
    }
}
