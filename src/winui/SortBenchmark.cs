using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private bool sortBenchmarkRunning;

    private void SortProcesses(int column)
    {
        model.Sort(column); processTable.SetSort(model.SortColumn, model.SortDescending); RefreshProcesses();
    }

    private static async Task NextRenderedFrame()
    {
        var ready = new TaskCompletionSource();
        void Rendered(object? sender, object args) => ready.TrySetResult();
        CompositionTarget.Rendering += Rendered;
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { CompositionTarget.Rendering -= Rendered; }
    }

    private static unsafe IEnumerable<ProcessRow> ReplayRows()
    {
        var rows = new List<ProcessRow>(10_000);
        for (uint i = 1; i <= 10_000; ++i) {
            string name = "Process " + (i * 37 % 997).ToString("D3", CultureInfo.InvariantCulture);
            var row = new ProcessRow(new ProcessKey(new(1, 2), i, GenerationKind.CreationTime, i));
            fixed (char* text = name) row.Update(new ProcessInfo {
                Name = new String16 { Data = text, Length = (uint)name.Length }, WorkingSetBytes = i * 7919 % 1_000_000,
                PrivateBytes = i * 3571 % 1_000_000, CpuPercent = new Metric { State = SampleState.Valid, Value = i * 31 % 100 },
                ReadIoBytesPerSecond = new Metric { State = SampleState.Valid, Value = i * 101 % 1000 },
                WriteIoBytesPerSecond = new Metric { State = SampleState.Valid, Value = i * 137 % 1000 }
            });
            rows.Add(row);
        }
        return rows;
    }

    private async void RunSortBenchmark()
    {
        sortBenchmarkRunning = true;
        string path = App.SortBenchmarkPath!;
        try {
            model.SetPaused(true); model.SetMode(ProcessMode.All); model.SetQuery(""); ShowView(false);
            using var output = new StreamWriter(path, false);
            await output.WriteLineAsync("scenario,rows,column,descending,handler_ms,layout_ms,two_frames_ms,icon_decodes_started");
            foreach (string scenario in new[] { "live", "replay" }) {
                if (scenario == "replay") model.ReplayForBenchmark(ReplayRows());
                SortProcesses(0); processList.UpdateLayout(); await NextRenderedFrame(); await NextRenderedFrame();
                var selected = model.VisibleProcesses.Skip(model.VisibleProcesses.Count / 2).Take(2).ToArray();
                foreach (var row in selected) processList.SelectedItems.Add(slotsByRow[row]);
                var primary = SelectedProcess;
                for (int round = 0; round < 2; ++round) for (int column = 0; column < 7; ++column) for (int direction = 0; direction < 2; ++direction) {
                    int decodedBefore = ProcessIconView.DecodedImagesCreated;
                    var watch = Stopwatch.StartNew();
                    var container = processList.ContainerFromIndex(0) as ListViewItem;
                    var visual = container is null ? null : Descendant<ProcessRowGrid>(container);
                    SortProcesses(column); double handler = watch.Elapsed.TotalMilliseconds;
                    processList.UpdateLayout(); double layout = watch.Elapsed.TotalMilliseconds;
                    if (container is not null && (!ReferenceEquals(container, processList.ContainerFromIndex(0))
                        || !ReferenceEquals(visual, Descendant<ProcessRowGrid>(container)))) throw new InvalidOperationException("Sorting recreated a visible process row.");
                    await NextRenderedFrame(); await NextRenderedFrame(); watch.Stop();
                    if (selected.Any(row => !processList.SelectedItems.Contains(slotsByRow[row]))) throw new InvalidOperationException("Sorting lost a selected process identity.");
                    if (SelectedProcess != primary) throw new InvalidOperationException("Sorting changed the primary selected process.");
                    await output.WriteLineAsync(string.Join(',', scenario, model.VisibleProcesses.Count, column, model.SortDescending,
                        handler.ToString("F3", CultureInfo.InvariantCulture), layout.ToString("F3", CultureInfo.InvariantCulture), watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                        ProcessIconView.DecodedImagesCreated - decodedBefore));
                    await output.FlushAsync();
                }
                processList.ScrollIntoView(slotsByRow[selected[0]], ScrollIntoViewAlignment.Leading);
                await NextRenderedFrame(); await NextRenderedFrame();
                var scroll = Descendant<ScrollViewer>(processList)!;
                var anchor = model.VisibleProcesses[Math.Min(model.VisibleProcesses.Count - 1, (int)(scroll.VerticalOffset / 34))];
                SortProcesses(1); processList.UpdateLayout(); await NextRenderedFrame(); await NextRenderedFrame();
                if (model.VisibleProcesses[Math.Min(model.VisibleProcesses.Count - 1, (int)(scroll.VerticalOffset / 34))] != anchor)
                    throw new InvalidOperationException("Sorting lost the scrolled process identity.");
            }
        } catch (Exception error) {
            File.AppendAllText(path + ".error.txt", error.ToString()); Environment.ExitCode = 1;
        } finally { Close(); }
    }
}
