using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

/// <summary>`--check-events=&lt;log&gt;`: the production events window over a live model, with real child processes.</summary>
internal sealed partial class ProcessEventsWindow
{
    internal static ProcessEventsWindow BeginUiChecks(string path)
    {
        var model = new MonitorModel(); model.SetInterval(250);
        var timeline = new ProcessTimeline();
        var capture = MainWindow.RunningElevated ? new ProcessCapture(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), timeline,
            () => (model.ProcessTime, model.BootIdentity.Identifier, model.BootIdentity.State)) : null;
        var window = new ProcessEventsWindow(timeline, capture);
        var tick = window.DispatcherQueue.CreateTimer(); tick.Interval = TimeSpan.FromMilliseconds(100);
        tick.Tick += (_, _) => { model.Tick(false); timeline.Observe(model); };
        tick.Start();
        window.Closed += (_, _) => { tick.Stop(); capture?.Dispose(); model.Dispose(); };
        async void Loaded(object sender, RoutedEventArgs args) { window.layout.Loaded -= Loaded; await window.CheckEventsUiAsync(path, model, timeline, capture); }
        window.layout.Loaded += Loaded;
        return window;
    }

    private static void EventCheck(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task WaitEventUiAsync(Func<bool> ready, TimeSpan timeout, string failure)
    {
        long began = Stopwatch.GetTimestamp();
        while (!ready()) {
            if (Stopwatch.GetElapsedTime(began) > timeout) throw new TimeoutException(failure);
            await Task.Delay(25);
        }
    }

    private async Task CheckEventsUiAsync(string path, MonitorModel model, ProcessTimeline timeline, ProcessCapture? capture)
    {
        void Log(string text) => File.AppendAllText(path, text + Environment.NewLine);
        var children = new List<Process>();
        bool passed = false, cleaned = true;
        try {
            bool elevated = MainWindow.RunningElevated;
            Log($"events-ui begin;elevated={elevated};aot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
            await WaitEventUiAsync(() => model.Processes.Count > 0, TimeSpan.FromSeconds(10), "The process model did not deliver a first snapshot.");
            await Task.Delay(1500);
            Refresh(true);
            EventCheck(visible.All(row => !row.Entry.Preexisting || !row.Started), "A process that was already running was listed as a start.");
            string parent = Process.GetCurrentProcess().ProcessName + ".exe";
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            for (int i = 0; i < 3; ++i)
                children.Add(Process.Start(new ProcessStartInfo(cmd) { Arguments = "/d /c ping -n 4 127.0.0.1 >nul", UseShellExecute = false, CreateNoWindow = true })
                    ?? throw new InvalidOperationException("Own cmd child did not start."));
            if (elevated) {
                await WaitEventUiAsync(() => capture!.State == ProcessEventMonitorState.Running, TimeSpan.FromSeconds(8), "The ETW capture did not reach the running state.");
                children.Add(Process.Start(new ProcessStartInfo(cmd) { Arguments = "/d /c exit 42", UseShellExecute = false, CreateNoWindow = true })!);
            }
            var pids = children.Select(child => (uint)child.Id).ToHashSet();
            await WaitEventUiAsync(() => timeline.Entries.Count(entry => pids.Contains(entry.Pid) && entry.Ended is not null) >= (elevated ? 4 : 3),
                TimeSpan.FromSeconds(15), "Own children did not appear as started and ended in the timeline.");
            Refresh(true);
            var own = timeline.Entries.Where(entry => pids.Contains(entry.Pid)).ToArray();
            foreach (var entry in own.Where(entry => entry.CommandLine.Contains("ping", StringComparison.OrdinalIgnoreCase))) {
                EventCheck(entry.ParentName == parent && entry.ParentPid == (uint)Environment.ProcessId, "A child lost its parent name: " + entry.ParentName);
                EventCheck(entry.CommandLine.Contains("ping -n 4", StringComparison.Ordinal), "A child lost its command line.");
                EventCheck(entry.Duration is { } length && length > TimeSpan.FromSeconds(1) && length < TimeSpan.FromSeconds(10), "A child has an implausible duration.");
                EventCheck(entry.Respawn >= 3, "Three identical children were not marked as repeated.");
            }
            EventCheck(own.Count(entry => entry.CommandLine.Contains("ping -n 4", StringComparison.Ordinal)) == 3, "Not all three observable children carry their command line.");
            foreach (var row in visible.Where(row => pids.Contains(row.Entry.Pid))) Log("row " + row);
            EventCheck(visible.Count(row => pids.Contains(row.Entry.Pid)) >= 6, "The table did not show a start and an end row for each child.");
            kinds.SelectedIndex = 3; Refresh(true);
            EventCheck(visible.Count > 0 && visible.All(row => row.Entry.Respawn >= 3), "The repeated filter did not keep only repeated starts.");
            kinds.SelectedIndex = 2; Refresh(true);
            EventCheck(visible.Count > 0 && visible.All(row => !row.Started), "The ended filter showed starts.");
            kinds.SelectedIndex = 0; search.Text = "ping -n 4"; Refresh(true);
            EventCheck(visible.Count(row => pids.Contains(row.Entry.Pid)) == 6, "Searching the command line did not return the six child rows.");
            var selected = visible[0];
            list.SelectedItem = selected; list.ScrollIntoView(selected);
            await WaitEventUiAsync(() => { list.UpdateLayout(); return list.ContainerFromItem(selected) is ListViewItem; }, TimeSpan.FromSeconds(3), "The row template was not realized.");
            var texts = EventTexts(list.ContainerFromItem(selected)).ToArray();
            EventCheck(texts.Contains(selected.TimeText) && texts.Contains(selected.ParentText) && texts.Contains(selected.CommandText) && texts.Contains(selected.DurationText),
                "The row template did not bind its clock time, parent, command line and duration.");
            Log("row texts " + string.Join(" | ", texts));
            if (elevated) {
                var exit = own.Single(entry => entry.ExitCode == 42);
                EventCheck(!exit.Alive, "The ETW exit code arrived without an end time.");
                Log($"etw exit=42;snapshotsMissed={exit.SnapshotsMissed};loss={capture!.Info?.ProviderEventsLost}");
                EventCheck(own.Where(entry => entry.CommandLine.Contains("ping", StringComparison.Ordinal)).All(entry => entry.ExitCode == 0), "ETW did not report the exit code of the ping children.");
                cleaned = false; cleaned = await capture.StopForCloseAsync();
                EventCheck(cleaned, "The ETW capture cleanup was not confirmed.");
            } else
                EventCheck(own.All(entry => entry.ExitCode is null), "Without ETW an exit code appeared.");
            passed = true;
        } catch (Exception error) {
            Log("FAIL events-ui " + error);
        } finally {
            foreach (var child in children) { try { if (!child.HasExited) child.Kill(); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } child.Dispose(); }
            bool success = passed && cleaned;
            Environment.ExitCode = success ? 0 : 1;
            Log($"{(success ? "PASS" : "FAIL")} events-ui finished;passed={passed};cleanup={cleaned}");
            Close();
        }
    }

    private static IEnumerable<string> EventTexts(DependencyObject root)
    {
        if (root is TextBlock text) yield return text.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
            foreach (var value in EventTexts(VisualTreeHelper.GetChild(root, i))) yield return value;
    }
}
