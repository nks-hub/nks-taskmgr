using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed partial class ProcessEventsWindow
{
    private Action<ProcessEventBatchInfo, int, int>? uiCheckBatch;
    private CancellationTokenSource? uiCheckCancellation;
    private bool uiCheckRunning;

    internal void BeginUiChecks(string path)
    {
        uiCheckCancellation = new(); uiCheckRunning = true;
        async void Loaded(object sender, RoutedEventArgs args) {
            layout.Loaded -= Loaded;
            await CheckEventsUiAsync(path);
        }
        if (layout.IsLoaded) DispatcherQueue.TryEnqueue(async () => await CheckEventsUiAsync(path));
        else layout.Loaded += Loaded;
    }

    private static void EventCheck(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CheckBatches
    {
        internal ProcessEventBatchInfo? Last;
        internal ulong EventsLost, BuffersLost, QueueDropped, DecodeErrors;
        internal bool Draining, CounterReset;
        internal int DrainLifecycle, DrainTraffic;
        internal readonly List<(int Lifecycle, int Traffic)> Drains = [];
        internal void Observe(ProcessEventBatchInfo info, int lifecycle, int traffic) {
            CounterReset |= info.ProviderEventsLost < EventsLost || info.ProviderBuffersLost < BuffersLost
                || info.QueueDropped < QueueDropped || info.DecodeErrors < DecodeErrors;
            EventsLost = Math.Max(EventsLost, info.ProviderEventsLost); BuffersLost = Math.Max(BuffersLost, info.ProviderBuffersLost);
            QueueDropped = Math.Max(QueueDropped, info.QueueDropped); DecodeErrors = Math.Max(DecodeErrors, info.DecodeErrors);
            Last = info;
            if (!Draining) return;
            DrainLifecycle += lifecycle; DrainTraffic += traffic;
            if (Drains.Count < 512) Drains.Add((lifecycle, traffic));
        }
        internal string Counters => $"eventsLost={EventsLost};buffersLost={BuffersLost};queueDropped={QueueDropped};decodeErrors={DecodeErrors};counterReset={CounterReset};resyncVersion={Last?.ResyncVersion};flags={Last?.Flags}";
    }

    private sealed class CheckChild(Process process) : IDisposable
    {
        internal ProcessKey Key;
        internal ulong RawCreated;
        internal int ExitCode;
        internal void Observe(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            EventCheck(GetEventChildTimes(process.SafeHandle, out ulong created, out _, out _, out _), "Own child creation FILETIME unavailable.");
            var identity = NativeMonitor.QueryProcessIdentity(process.SafeHandle, GenerationKind.CreationTime);
            EventCheck(identity.State == SampleState.Valid && identity.Kind == GenerationKind.CreationTime && identity.Generation == created
                && identity.Pid == (uint)process.Id && (identity.BootLow | identity.BootHigh) != 0 && created != 0,
                "Own held child identity disagrees with independent GetProcessTimes.");
            Key = new(new(identity.BootLow, identity.BootHigh), identity.Pid, identity.Kind, identity.Generation); RawCreated = created;
            long began = Stopwatch.GetTimestamp();
            while (!process.WaitForExit(25)) {
                cancellationToken.ThrowIfCancellationRequested();
                EventCheck(Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(3), "Own cmd child exceeded its three-second deadline.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            ExitCode = process.ExitCode; EventCheck(ExitCode == 42, "Own cmd child did not exit with fixture code 42.");
        }
        public void Dispose() {
            try {
                if (!process.HasExited) {
                    EventCheck(TerminateEventChild(process.SafeHandle, 0xffff), "Owned child abort cleanup failed.");
                    EventCheck(process.WaitForExit(2000), "Owned child did not exit after abort cleanup.");
                }
            } finally { process.Dispose(); }
        }
    }

    private static async Task WaitEventUiAsync(Func<bool> ready, TimeSpan timeout, string failure, CancellationToken cancellationToken)
    {
        long began = Stopwatch.GetTimestamp();
        while (!ready()) {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(began) > timeout) throw new TimeoutException(failure);
            await Task.Delay(25, cancellationToken);
        }
    }

    private async Task CheckEventsUiAsync(string path)
    {
        void Log(string text) => File.AppendAllText(path, text + Environment.NewLine);
        var batches = new CheckBatches();
        var children = new List<CheckChild>(300);
        bool passed = false, cleaned = false, childCleanup = true;
        CancellationToken cancellationToken = uiCheckCancellation!.Token;
        uiCheckBatch = batches.Observe;
        try {
            cancellationToken.ThrowIfCancellationRequested();
            bool elevated = EventCheckElevated();
            Log($"events-ui begin;elevated={elevated};aot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
            EventCheck(start.IsEnabled && clear.IsEnabled && modes.Items.Count == 3, "Actual idle event controls are not ready.");
            EventCheck(list.ContextFlyout is null && detail.IsReadOnly, "Historical event controls exposed a live action or editable detail.");
            StartCapture();
            await WaitEventUiAsync(() => batches.Last?.State is ProcessEventMonitorState.Running or ProcessEventMonitorState.AccessDenied
                or ProcessEventMonitorState.Unsupported or ProcessEventMonitorState.Error or ProcessEventMonitorState.CleanupFailed,
                TimeSpan.FromSeconds(8), "Actual event window did not reach a definitive capture state.", cancellationToken);
            var initial = batches.Last ?? throw new InvalidOperationException("The actual event window did not report batch metadata.");
            Log($"events-ui initial={initial.State};win32={initial.Win32Error};{batches.Counters}");
            if (initial.State is ProcessEventMonitorState.AccessDenied or ProcessEventMonitorState.Unsupported) {
                EventCheck(!elevated && initial.Win32Error != 0, "Elevated check did not obtain real capture or denial lacked its native error.");
                EventCheck(status.Text.Contains(initial.State == ProcessEventMonitorState.AccessDenied ? "zamítnutý" : "podporovaný", StringComparison.Ordinal)
                    && start.IsEnabled && !stop.IsEnabled, "Actual denied/unsupported UI did not preserve the failure and available retry.");
                modes.SelectedIndex = 2; await Task.Delay(100, cancellationToken);
                EventCheck(list.ItemTemplate == (DataTemplate)templates["ProcessLifetime"] && visible.Count == 0,
                    "Denied capture invented a process lifetime or did not switch its real template.");
                EventCheck(await StopForCloseAsync(), "Denied capture cleanup was not confirmed."); cleaned = true;
                cancellationToken.ThrowIfCancellationRequested();
                CancelClose();
                Log($"events-ui verified explicit={initial.State};captureClaimed=False;{batches.Counters}"); passed = true;
            } else {
                EventCheck(initial.State == ProcessEventMonitorState.Running, "Actual event window failed to capture.");
                EventCheck(!start.IsEnabled && stop.IsEnabled && status.Text.Contains("Záznam běží", StringComparison.Ordinal),
                    "Actual running status and Start/Stop controls disagree with capture state.");
                timer.Stop();
                int retainedBefore = retained.Count;
                Log("events-ui running;only-this-window-timer=stopped;creatingOwnChildren=300");
                await Task.Run(() => {
                    for (int i = 0; i < 300; ++i) {
                        cancellationToken.ThrowIfCancellationRequested();
                        var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) {
                            Arguments = "/d /c exit 42", UseShellExecute = false, CreateNoWindow = true
                        }) ?? throw new InvalidOperationException("Own cmd child did not start.");
                        var own = new CheckChild(process); children.Add(own); own.Observe(cancellationToken);
                    }
                }, cancellationToken);
                await Task.Delay(1500, cancellationToken);
                EventCheck(!timer.IsRunning && retained.Count == retainedBefore, "The paused event window read its backlog before StopForClose.");
                batches.Draining = true;
                EventCheck(await StopForCloseAsync(), "Actual StopForClose did not confirm cleanup and drain."); cleaned = true;
                cancellationToken.ThrowIfCancellationRequested();
                batches.Draining = false;
                EventCheck(state == ProcessEventMonitorState.Stopped && status.Text.Contains("Záznam zastavený", StringComparison.Ordinal)
                    && !NeedsCleanup && batches.Drains.Count >= 3
                    && batches.Drains[0].Lifecycle == 512 && batches.DrainLifecycle >= 600
                    && batches.Drains[^1] == (0, 0), "Stopped lifecycle backlog was not drained across the 512-record frame boundary.");
                EventCheck(!batches.CounterReset && batches.EventsLost == 0 && batches.BuffersLost == 0 && batches.QueueDropped == 0 && batches.DecodeErrors == 0,
                    "The real trace reported loss or decoding failure; this is not a complete own-child capture.");
                EventCheck(!start.IsEnabled && !stop.IsEnabled && !clear.IsEnabled, "Close drain re-enabled a command before cancellation.");
                CancelClose();
                EventCheck(start.IsEnabled && clear.IsEnabled && !stop.IsEnabled, "Cancelling the test close owner did not restore the stopped controls.");
                Log($"events-ui drained;lifecycle={batches.DrainLifecycle};traffic={batches.DrainTraffic};batches={string.Join(',', batches.Drains.Select(value => value.Lifecycle + "/" + value.Traffic))};{batches.Counters}");
                await CheckOwnLifetimeUiAsync(children, Log, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Log($"events-ui verified capture=True;ownChildren={children.Count};ownEndpoints={children.Count * 2};cleanup=True;{batches.Counters}"); passed = true;
            }
        } catch (Exception error) {
            Log("FAIL events-ui " + error);
        } finally {
            timer.Stop(); filterTimer.Stop();
            foreach (var child in children) {
                try { child.Dispose(); }
                catch (Exception error) { childCleanup = false; Log("FAIL owned child cleanup " + error.Message); }
            }
            if (!cleaned || NeedsCleanup) {
                try { cleaned = await StopForCloseAsync(); }
                catch (Exception error) { Log("FAIL final event cleanup " + error.Message); }
            }
            uiCheckBatch = null;
            bool success = passed && cleaned && childCleanup;
            success &= !cancellationToken.IsCancellationRequested;
            Environment.ExitCode = success ? 0 : 1;
            Log($"{(success ? "PASS" : "FAIL")} events-ui finished;passed={success};cleanup={cleaned};childrenClean={childCleanup};cancelled={cancellationToken.IsCancellationRequested}");
            uiCheckRunning = false; uiCheckCancellation?.Dispose(); uiCheckCancellation = null;
            if (cleaned) CloseAfterStop();
            else Log("FAIL events-ui remains open because own ETW cleanup was not confirmed.");
        }
    }

    private async Task CheckOwnLifetimeUiAsync(IReadOnlyList<CheckChild> children, Action<string> log, CancellationToken cancellationToken)
    {
        var keys = children.Select(child => child.Key).ToHashSet();
        EventCheck(keys.Count == children.Count, "Own fixture reused a full held lifetime key.");
        foreach (var child in children) {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = lifetimes.Values.Where(row => row.Lifetime?.Key == child.Key).ToArray();
            EventCheck(matches.Length == 1, "An own verified lifetime was missing or duplicated in the actual window store.");
            var entry = matches[0].Lifetime!;
            var first = entry.Start ?? throw new InvalidOperationException("Own lifetime Start was not captured.");
            var last = entry.Stop ?? throw new InvalidOperationException("Own lifetime Stop was not captured.");
            double duration = entry.DurationSeconds ?? throw new InvalidOperationException("Own lifetime QPC duration was not available.");
            EventCheck(entry.Paired && duration >= 0 && last.Event.Flags.HasFlag(ProcessEventFlags.HasExitStatus) && last.Event.ExitStatus == child.ExitCode,
                "An own lifetime lacked its actual pair, QPC duration or independently observed exit code.");
            log($"own lifetime boot={child.Key.Boot.High:X16}{child.Key.Boot.Low:X16};pid={child.Key.Pid};rawCT={child.RawCreated};startQpc={first.Event.TimestampQpc};stopQpc={last.Event.TimestampQpc};frequency={first.Frequency};duration={duration.ToString("R", CultureInfo.InvariantCulture)};exit={child.ExitCode}");
        }
        int rawCount = retained.Count;
        EventCheck(retained.Count(row => children.Any(child => row.Source.Pid == child.Key.Pid && row.Source.Generation == child.Key.Generation
            && row.Source.IdentityKind == GenerationKind.CreationTime && row.Source.IdentityState == SampleState.Valid)) == 600,
            "The raw view did not retain both own verified endpoints.");
        modes.SelectedIndex = 2;
        await Task.Delay(100, cancellationToken);
        EventCheck(kinds.Items.Count == 8 && list.ItemTemplate == (DataTemplate)templates["ProcessLifetime"], "Actual lifetime mode/filter/template did not activate.");
        kinds.SelectedIndex = 1; search.Text = "";
        await Task.Delay(200, cancellationToken);
        EventCheck(visible.Count(row => row.Lifetime?.Key is { } key && keys.Contains(key)) == 300,
            "Completed-lifetime filtering hid an own paired lifetime.");
        var selected = visible.First(row => row.Lifetime?.Key == children[0].Key);
        list.SelectedItem = selected; list.ScrollIntoView(selected);
        await WaitEventUiAsync(() => { list.UpdateLayout(); return list.ContainerFromItem(selected) is ListViewItem; },
            TimeSpan.FromSeconds(3), "The real lifetime row template was not realized.", cancellationToken);
        var container = (ListViewItem)list.ContainerFromItem(selected);
        var texts = EventTexts(container).ToArray();
        EventCheck(texts.Contains(selected.StartedText) && texts.Contains(selected.EndedText) && texts.Contains(selected.DurationText),
            "The real lifetime template did not bind its start, stop and duration.");
        EventCheck(ReferenceEquals(list.SelectedItem, selected) && detail.Text.Contains("PID " + children[0].Key.Pid, StringComparison.Ordinal)
            && detail.Text.Contains("0x0000002A", StringComparison.Ordinal) && detail.Text.Contains("Žádné živé akce", StringComparison.Ordinal),
            "Actual lifetime selection did not show its own read-only endpoint details.");
        search.Text = "PID " + children[0].Key.Pid;
        await Task.Delay(200, cancellationToken);
        EventCheck(visible.Contains(selected) && ReferenceEquals(list.SelectedItem, selected), "Filtering lost the selected own lifetime key.");
        hold.IsChecked = true;
        var held = visible.ToArray(); search.Text = "no-match-" + Guid.NewGuid().ToString("N");
        await Task.Delay(200, cancellationToken);
        EventCheck(visible.SequenceEqual(held), "Holding the real view did not freeze its rows during filter edits.");
        hold.IsChecked = false; await Task.Delay(100, cancellationToken);
        EventCheck(visible.Count == 0, "Releasing the real held view did not apply the edited filter.");
        search.Text = ""; await Task.Delay(200, cancellationToken);
        kinds.SelectedIndex = 2; await Task.Delay(50, cancellationToken);
        EventCheck(!visible.Any(row => row.Lifetime?.Key is { } key && keys.Contains(key)), "Paired own children appeared as lifetimes without an end.");
        modes.SelectedIndex = 0; await Task.Delay(100, cancellationToken);
        EventCheck(list.ItemTemplate == (DataTemplate)templates["ProcessEvent"] && retained.Count == rawCount, "Lifetime mode replaced or discarded the raw event view.");
        modes.SelectedIndex = 1; await Task.Delay(100, cancellationToken);
        EventCheck(trafficSupported && trafficInfo is not null && list.ItemTemplate == (DataTemplate)templates["ProcessEvent"], "Lifetime mode broke the retained traffic view.");
        log($"events-ui realControls=True;realLifetimeTemplate=True;selection=True;filters=True;hold=True;rawPreserved={rawCount};trafficPreserved={trafficRows.Count};pixelAcceptance=False");
    }

    private static IEnumerable<string> EventTexts(DependencyObject root)
    {
        if (root is TextBlock text) yield return text.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
            foreach (var value in EventTexts(VisualTreeHelper.GetChild(root, i))) yield return value;
    }

    private static bool EventCheckElevated()
    {
        EventCheck(OpenEventCheckToken(new nint(-1), 8, out var token), "Own UI token unavailable.");
        using (token) {
            EventCheck(GetEventCheckElevation(token, 20, out uint elevation, sizeof(uint), out _), "Own UI token elevation unavailable.");
            return elevation != 0;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetEventChildTimes(SafeProcessHandle process, out ulong created, out ulong ended, out ulong kernel, out ulong user);
    [LibraryImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TerminateEventChild(SafeProcessHandle process, uint code);
    [LibraryImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenEventCheckToken(nint process, uint access, out SafeFileHandle token);
    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetEventCheckElevation(SafeFileHandle token, int type, out uint value, uint length, out uint used);
}
