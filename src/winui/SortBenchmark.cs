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
        bool iconsEnabled = !LaunchArguments.Current.Contains("--bench-sort-no-icons", StringComparer.Ordinal);
        ProcessIconView.SuppressForBenchmark = !iconsEnabled;
        string path = App.SortBenchmarkPath!;
        try {
            if (model.BootIdentity.State != SampleState.Valid || model.BootIdentity.Identifier.IsEmpty)
                throw new InvalidOperationException("Sort benchmark requires a verified boot identity.");
            model.SetPaused(true); model.SetMode(ProcessMode.All); model.SetQuery(""); ShowView(false);
            using var output = new StreamWriter(path, false);
            using var interaction = new StreamWriter(path + ".interaction.csv", false);
            using var interactionSummary = new StreamWriter(path + ".interaction-summary.csv", false);
            await interaction.WriteLineAsync("scenario,rows,case,iteration,handler_ms,layout_ms,two_frames_ms,query_ms,refresh_ms,slots_assign_ms,slots_restore_ms,slots_focus_ms,visible_rows");
            await interactionSummary.WriteLineAsync("scenario,rows,case,samples,p95_handler_ms,max_handler_ms,p95_two_frames_ms,max_two_frames_ms,target_ms,target_met");
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
                await RunInteractionBenchmark(scenario, interaction, interactionSummary);
            }
        } catch (Exception error) {
            File.AppendAllText(path + ".error.txt", error.ToString()); Environment.ExitCode = 1;
        } finally { RequestMainClose(); }
    }

    private static double Percentile95(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
    }

    private async Task<(double Handler, double Layout, double Total)> TimeInteraction(Action action)
    {
        var watch = Stopwatch.StartNew();
        action(); double handler = watch.Elapsed.TotalMilliseconds;
        processList.UpdateLayout(); double layout = watch.Elapsed.TotalMilliseconds;
        await NextRenderedFrame(); await NextRenderedFrame();
        return (handler, layout, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>Measures search and selection latency (handler plus two rendering callbacks) and reports p95/max per case against the T44 targets without enforcing them.</summary>
    private async Task RunInteractionBenchmark(string scenario, StreamWriter samples, StreamWriter summary)
    {
        const int iterations = 20;
        const double searchTargetMs = 100, selectionTargetMs = 50;
        static string F(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
        var results = new Dictionary<string, (List<double> Handler, List<double> Total, double Target)>();
        double queryMs = 0, refreshMs = 0;
        int rows = model.VisibleProcesses.Count;
        if (rows < 2 || processSlots.Count != rows) throw new InvalidOperationException("Interaction benchmark requires a bound process list with at least two rows.");
        async Task Measure(string name, double target, int iteration, Action action, Action verify)
        {
            var (handler, layout, total) = await TimeInteraction(action);
            verify();
            if (!results.TryGetValue(name, out var list)) results[name] = list = (new List<double>(), new List<double>(), target);
            list.Handler.Add(handler); list.Total.Add(total);
            await samples.WriteLineAsync(string.Join(',', scenario, rows, name, iteration, F(handler), F(layout), F(total), F(queryMs), F(refreshMs),
                F(slotsAssignMs), F(slotsRestoreMs), F(slotsFocusMs), model.VisibleProcesses.Count));
            await samples.FlushAsync();
        }
        var middle = model.VisibleProcesses[rows / 2];
        (string Case, string Query)[] queries = [("prefix", middle.Name.Length > 1 ? middle.Name[..^1] : middle.Name), ("pid", middle.PidText), ("no_match", "no-such-process-zz"), ("clear", "")];
        for (int i = 0; i < iterations; ++i) foreach (var (name, query) in queries)
            await Measure("search_" + name, searchTargetMs, i, () => {
                long began = Stopwatch.GetTimestamp(); processQuery = query; model.SetQuery(query);
                long queried = Stopwatch.GetTimestamp(); RefreshProcesses();
                queryMs = Stopwatch.GetElapsedTime(began, queried).TotalMilliseconds; refreshMs = Stopwatch.GetElapsedTime(queried).TotalMilliseconds;
            }, () => {
                int count = model.VisibleProcesses.Count;
                bool applied = processSlots.Count == count && name switch {
                    "no_match" => count == 0, "clear" => count == rows, _ => count > 0 && model.VisibleProcesses.Contains(middle)
                };
                if (!applied) throw new InvalidOperationException($"Search '{name}' did not apply the filtered process list.");
                var expected = model.Processes.Where(row => query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || row.PidText.Contains(query, StringComparison.Ordinal)).ToHashSet();
                if (!expected.SetEquals(model.VisibleProcesses) || !processSlots.Select(slot => slot.Row).SequenceEqual(model.VisibleProcesses)
                    || slotsByRow.Count != count || model.VisibleProcesses.Any(row => !ReferenceEquals(slotsByRow[row].Row, row)))
                    throw new InvalidOperationException($"Search '{name}' produced rows or slots that differ from the reference filter.");
            });
        var scroll = Descendant<ScrollViewer>(processList)!;
        async Task ResetSelection()
        {
            SelectProcess(processSlots[0].Row); processList.UpdateLayout(); await NextRenderedFrame(); await NextRenderedFrame();
        }
        async Task Select(string name, int iteration, int index, bool scrollIntoView)
        {
            index = Math.Min(index, rows - 1);
            var slot = processSlots[index];
            await Measure(name, selectionTargetMs, iteration, () => {
                SelectProcess(slot.Row, false);
                if (scrollIntoView) processList.ScrollIntoView(slot);
            }, () => {
                if (!ReferenceEquals(processList.SelectedItem, slot) || SelectedProcess != slot.Row) throw new InvalidOperationException($"Selection '{name}' did not select the requested process.");
            });
        }
        await ResetSelection();
        for (int i = 0; i < iterations; ++i) await Select("select_click_visible", i, (int)(scroll.VerticalOffset / ProcessTable.RowHeight) + 2 + i % 4 * 2, false);
        await ResetSelection();
        for (int i = 0; i < iterations; ++i) await Select("select_step_1", i, i + 1, true);
        await ResetSelection();
        for (int i = 0; i < iterations; ++i) await Select("select_step_5", i, 5 * (i + 1), true);
        await ResetSelection();
        for (int i = 0; i < iterations; ++i) await Select("select_jump_scroll", i, i % 2 == 0 ? rows * 3 / 4 : 0, true);
        foreach (var (name, (handler, total, target)) in results) {
            double p95Total = Percentile95(total);
            await summary.WriteLineAsync(string.Join(',', scenario, rows, name, total.Count, F(Percentile95(handler)), F(handler.Max()), F(p95Total), F(total.Max()), F(target), p95Total <= target));
        }
        await summary.FlushAsync();
        await ResetSelection();
    }
}
