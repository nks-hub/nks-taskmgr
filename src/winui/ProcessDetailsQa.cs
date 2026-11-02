using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    internal async void BeginProcessDetailsChecks(string logPath, Func<bool> hasUnhandledError)
    {
        ProcessDetailsView? details = null;
        void Log(string message) => File.AppendAllText(logPath, message + Environment.NewLine);
        try {
            long deadline = Environment.TickCount64 + 10000;
            ProcessRow? own;
            while ((own = model.Processes.FirstOrDefault(row => row.Key.Pid == Environment.ProcessId && CanReadDetails(row))) is null) {
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("Own current process was not collected.");
                await Task.Delay(100);
            }
            var selected = own;
            ShowView(DesktopView.Processes); SelectProcessMode(ProcessMode.All);
            search.Text = selected.PidText;
            deadline = Environment.TickCount64 + 10000;
            while (!model.VisibleProcesses.Contains(selected) || !slotsByRow.ContainsKey(selected)) {
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("The actual process table did not expose the selected process.");
                await Task.Delay(100);
            }
            SelectProcess(selected); UpdateProcessDetailsCommand();
            if (!ReferenceEquals(SelectedProcess, selected) || !processDetailsButton.IsEnabled)
                throw new InvalidOperationException("The actual process details command has no current selection.");
            new ButtonAutomationPeer(processDetailsButton).Invoke();
            details = processDetailsWindow ?? throw new InvalidOperationException("The actual process details command did not open its window.");
            Log("process-details actualButton=True;selection=True");
            Activate();
            await details.CheckUiAsync(Log);
            WindowOwnership.Minimize(details);
            if (!WindowOwnership.IsMinimized(details)) throw new InvalidOperationException("The actual details window did not minimize.");
            new ButtonAutomationPeer(processDetailsButton).Invoke();
            if (!ReferenceEquals(details, processDetailsWindow) || WindowOwnership.IsMinimized(details))
                throw new InvalidOperationException("The actual process details command did not restore its existing window.");
            Log("process-details restored=True;reused=True");
            if (!WindowOwnership.IsOwnedBy(details, this) || !WindowOwnership.IsAboveOwner(details, this))
                throw new InvalidOperationException("Process details lost their owner or moved below the main window.");
            Log("process-details owner=True;aboveOwner=True");
            details.Close(); details = null;

            var changed = selected.Key with { Generation = selected.Key.Generation == ulong.MaxValue ? selected.Key.Generation - 1 : selected.Key.Generation + 1 };
            details = new ProcessDetailsView(processDetailsClient, changed, selected.Name, () => true);
            details.ShowOwned(this);
            await details.CheckRejectedTargetAsync(Log);
            if (hasUnhandledError()) throw new InvalidOperationException("Process details raised an unhandled UI error.");
            Log("Process details checks passed");
        } catch (Exception error) { Log(error.ToString()); Environment.ExitCode = 1; }
        finally { details?.Close(); Close(); }
    }
}

internal sealed partial class ProcessDetailsView
{
    internal async Task CheckUiAsync(Action<string> log)
    {
        await WaitForDetailsAsync(() => activated && operation is null, "Initial details request");
        foreach (var selectedMode in new[] { ProcessDetailsCommand.Threads, ProcessDetailsCommand.Modules, ProcessDetailsCommand.Memory, ProcessDetailsCommand.Handles }) {
            await SelectModeAsync(selectedMode);
            await WaitForDetailsAsync(() => operation is null && filterOperation is null, "Details mode request");
            var snapshot = SnapshotFor(selectedMode) ?? throw new InvalidOperationException("Actual details request failed: " + state.Text);
            int expected = selectedMode switch {
                ProcessDetailsCommand.Threads => snapshot.Threads.Length, ProcessDetailsCommand.Modules => snapshot.Modules.Length,
                ProcessDetailsCommand.Memory => snapshot.Memory.Length, ProcessDetailsCommand.Handles => snapshot.Handles.Length, _ => 0
            };
            if (expected == 0 || list.Items.Count != expected) throw new InvalidOperationException("Actual details response did not reach the list.");
            list.ScrollIntoView(list.Items[0]);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await DetailsRenderedAsync();
            list.UpdateLayout();
            var first = list.Items[0] as ProcessDetailsRow;
            var container = list.ContainerFromIndex(0) as ListViewItem;
            if (first is null || container is null || !DetailsText(container).Any(text => text.Text == first.Text && text.ActualWidth > 0 && text.ActualHeight > 0))
                throw new InvalidOperationException("Actual process details text was blank after GC.");
            if (!ReferenceEquals(SelectedRow, first.Source) || detail.Text.Length == 0 || detail.Text == "Vyberte řádek ze seznamu.")
                throw new InvalidOperationException("Selected details row did not reach the inspector.");
            log($"process-details mode={selectedMode};generation={Key.Kind};nativeRows={expected};visibleText=True;inspector=True;gc=True");
            string query = selectedMode switch {
                ProcessDetailsCommand.Threads => ((ProcessThreadInfo)first.Source).Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ProcessDetailsCommand.Modules => ((ProcessModuleInfo)first.Source).Path,
                ProcessDetailsCommand.Memory => ((ProcessMemoryRegion)first.Source).RegionSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ProcessDetailsCommand.Handles => "0x" + ((ProcessHandleInfo)first.Source).Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture), _ => ""
            };
            if (query.Length == 0) query = first.Text;
            find.Text = query;
            await WaitForDetailsAsync(() => !searchTimer.IsRunning && filterOperation is null, "Source field filter");
            if (!list.Items.OfType<ProcessDetailsRow>().Any(row => ReferenceEquals(row.Source, first.Source)))
                throw new InvalidOperationException("Filtering lost the actual source row.");
            find.Text = "no-match-" + Guid.NewGuid().ToString("N");
            await WaitForDetailsAsync(() => !searchTimer.IsRunning && filterOperation is null, "Empty filter");
            if (list.Items.Count != 0 || detail.Text != "Seznam je prázdný. Zkontrolujte hledání a filtr.")
                throw new InvalidOperationException("An empty result retained a selection or blank inspector.");
            find.Text = "";
            await WaitForDetailsAsync(() => !searchTimer.IsRunning && filterOperation is null, "Reset filter");
            if (list.Items.Count != expected || SelectedRow is null || detail.Text.Length == 0)
                throw new InvalidOperationException("Resetting the filter did not restore the actual snapshot.");
            log($"process-details filter={selectedMode};sourceRow=True;empty=True;reset=True");
        }
    }

    internal async Task CheckRejectedTargetAsync(Action<string> log)
    {
        await WaitForDetailsAsync(() => activated && operation is null, "Changed-generation details request");
        if (!targetChanged || threadSnapshot is not null || list.Items.Count != 0 || state.Text.Length == 0)
            throw new InvalidOperationException("A changed process generation was not explicitly rejected.");
        log("process-details changedGenerationRejected=True;statusVisible=True");
    }

    private static async Task WaitForDetailsAsync(Func<bool> ready, string stage)
    {
        long deadline = Environment.TickCount64 + 10000;
        while (!ready()) {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException(stage);
            await Task.Delay(100);
        }
    }

    private static async Task DetailsRenderedAsync()
    {
        var ready = new TaskCompletionSource(); int frames = 0;
        void Rendered(object? sender, object args) { if (++frames == 2) ready.TrySetResult(); }
        CompositionTarget.Rendering += Rendered;
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= Rendered; }
    }

    private static IEnumerable<TextBlock> DetailsText(DependencyObject root)
    {
        if (root is TextBlock text) yield return text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
            foreach (var child in DetailsText(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
