using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static class ApplicationGroupTemplateChecks
{
    internal static async Task RunAsync(ListView productionGroups, ComboBox productionChoice, Action<string> log)
    {
        Require(!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported, "Application template checks require Native AOT.");
        await Rendered();
        var source = new ApplicationGroupModel();
        var process = new ProcessRow(new ProcessKey(new(1, 2), 10, GenerationKind.CreationTime, 100));
        SetCpu(process, 1);
        Apply(source, process, "Projekt A");
        var group = source.Visible.Single();
        var list = productionGroups; var choice = productionChoice;
        var originalList = list.ItemsSource; var originalChoice = choice.ItemsSource;
        var originalListSelection = list.SelectedItem; var originalChoiceSelection = choice.SelectedItem;
        var listVisibility = list.Visibility; var choiceVisibility = choice.Visibility;
        var releaseView = MainWindow.PreserveApplicationGroupView(source, list, choice);
        try {
            log("application-template phase=production-controls");
            list.ItemsSource = choice.ItemsSource = source.Visible; choice.SelectedItem = group;
            list.Visibility = choice.Visibility = Visibility.Visible;
            log("application-template phase=gc");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            log("application-template phase=render");
            await Rendered();
            CheckRow(list, group, "initial", log);
            CheckChoice(choice, group, "initial", log);

            Apply(source, process, "Projekt B");
            Require(ReferenceEquals(source.Visible.Single(), group), "An application title update replaced the bound row.");
            await Rendered();
            CheckRow(list, group, "title changed", log);
            CheckChoice(choice, group, "title changed", log);

            string previousSummary = group.Summary;
            SetCpu(process, 25); source.RefreshMetrics(false);
            Require(group.Summary != previousSummary, "The application fixture did not change its CPU summary.");
            await Rendered();
            CheckRow(list, group, "summary changed", log);

            list.ItemsSource = null; choice.ItemsSource = null;
            await Rendered();
            list.ItemsSource = choice.ItemsSource = source.Visible; choice.SelectedItem = group;
            await Rendered();
            CheckRow(list, group, "rebound", log);
            CheckChoice(choice, group, "rebound", log);
            await CheckSorting(source, process, list, choice, log);
            log("application-template aot=True;initial=True;titleUpdate=True;summaryUpdate=True;rebound=True;choice=True");
        } finally {
            releaseView();
            list.ItemsSource = originalList; choice.ItemsSource = originalChoice;
            list.SelectedItem = originalListSelection; choice.SelectedItem = originalChoiceSelection;
            list.Visibility = listVisibility; choice.Visibility = choiceVisibility;
        }
    }

    private static void Apply(ApplicationGroupModel source, ProcessRow process, string title)
        => ApplyGroups(source, [process], (process, title));

    private static void ApplyGroups(ApplicationGroupModel source, ProcessRow[] processes, params (ProcessRow Row, string Title)[] windows)
    {
        source.Apply(new ProcessDetailsSnapshot(ProcessDetailsCommand.Applications, ProcessDetailsState.Ok, 0, 0,
            ProcessArchitecture.X64, false, [], [], null, DateTimeOffset.UtcNow) {
            Applications = windows.Select((item, index) => new ProcessApplicationInfo((ulong)(index + 1), item.Row.Key, null, item.Title, "", "", SampleState.Unavailable, SampleState.Unavailable)).ToArray()
        });
        source.Refresh(processes, "");
    }

    private static void SetCpu(ProcessRow process, double cpu, ulong memory = 0, uint parent = 0)
    {
        process.SetCreatedTime(process.Key.Generation);
        process.Update(new ProcessInfo {
            Pid = process.Key.Pid, ParentPid = parent, Generation = process.Key.Generation, GenerationKind = process.Key.Kind, WorkingSetBytes = memory,
            CpuPercent = new Metric { Value = cpu, State = SampleState.Valid, Source = MetricSource.NativeProcess }
        });
    }

    private static async Task CheckSorting(ApplicationGroupModel source, ProcessRow process, ListView list, ComboBox choice, Action<string> log)
    {
        var second = new ProcessRow(new(process.Key.Boot, 11, GenerationKind.CreationTime, 200));
        var third = new ProcessRow(new(process.Key.Boot, 12, GenerationKind.CreationTime, 300));
        var child = new ProcessRow(new(process.Key.Boot, 13, GenerationKind.CreationTime, 400));
        SetCpu(process, 25, 20); SetCpu(second, 5, 50); SetCpu(third, 5, 10); SetCpu(child, 0, 2, second.Key.Pid);
        ApplyGroups(source, [process, second, third, child], (process, "Beta"), (second, "Alpha"), (third, "Alpha"));
        var selected = source.Visible.Single(row => row.Key == second.Key);
        source.Select(selected.Key); list.SelectedItem = choice.SelectedItem = selected;
        void Order(params uint[] pids) {
            Require(source.Visible.Select(row => row.Key.Pid).SequenceEqual(pids), "Application sort order or full-key tie break differs.");
            Require(ReferenceEquals(source.Selected, selected) && ReferenceEquals(list.SelectedItem, selected)
                && ReferenceEquals(choice.SelectedItem, selected), "Application sorting lost the selected group identity.");
        }
        source.SetSort(ApplicationGroupSort.Name, false); Order(11, 12, 10);
        source.SetSort(ApplicationGroupSort.Name, true); Order(10, 11, 12);
        source.SetSort(ApplicationGroupSort.Cpu, true); Order(10, 11, 12);
        SetCpu(third, 50, 10); source.RefreshMetrics(false); Order(12, 10, 11);
        SetCpu(third, 1, 10); source.RefreshMetrics(true); Order(12, 10, 11);
        source.RefreshMetrics(false); Order(10, 11, 12);
        source.SetSort(ApplicationGroupSort.Memory, true); Order(11, 10, 12);
        source.SetSort(ApplicationGroupSort.Processes, true); Order(11, 10, 12);
        var beta = source.Visible.Single(row => row.Key == process.Key);
        source.Select(beta.Key); list.SelectedItem = choice.SelectedItem = beta;
        source.Refresh([process, second, third, child], "Alpha"); Order(11, 12);
        Require(selected.Contains(child.Key), "Searching the application list changed verified process membership.");
        Require(new[] { process, second, third, child }.Where(source.Contains).Select(row => row.Key.Pid).SequenceEqual([11u, 13u]),
            "Filtering Beta out did not switch the process-table membership to the selected Alpha group.");
        source.Refresh([process, second, third, child], "no matching application");
        Require(source.Visible.Count == 0 && source.Selected is null && list.SelectedItem is null && choice.SelectedItem is null
            && !new[] { process, second, third, child }.Any(source.Contains), "An empty application filter retained a model, UI or process-table selection.");
        source.Refresh([process, second, third, child], "Alpha"); Order(11, 12);
        source.Refresh([process, second, third, child], ""); Order(11, 10, 12);
        await Rendered(); CheckRow(list, selected, "sorted selection", log);
        Require(ReferenceEquals(selected.IconRow, second) && selected.IconRow is { } icon && icon.Key == selected.Key && icon.Created100ns == 200,
            "The application icon is not tied to the exact root process lifetime.");
        ApplyGroups(source, [process, second, third, child], (process, new string('x', 140) + " first-window-needle"),
            (process, new string('y', 140) + " other-window-needle"), (second, "Alpha"), (third, "Alpha"));
        source.Refresh([process, second, third, child], "first-window-needle");
        selected = source.Visible.Single(); Order(10);
        Require(!selected.Title.Contains("first-window-needle", StringComparison.Ordinal) && selected.Windows.Length == 2,
            "The long-title search fixture did not place its match beyond display truncation.");
        source.Refresh([process, second, third, child], "other-window-needle"); Order(10);

        var rows = Enumerable.Range(0, 20).Select(index => new ProcessRow(new(process.Key.Boot, (uint)(100 + index), GenerationKind.CreationTime, (ulong)(1000 + index)))).ToArray();
        foreach (var row in rows) SetCpu(row, 1);
        source.SetSort(ApplicationGroupSort.Name, false);
        ApplyGroups(source, rows, rows.Select((row, index) => (row, "Application " + index.ToString("D2"))).ToArray());
        selected = source.Visible[10]; source.Select(selected.Key); list.SelectedItem = choice.SelectedItem = selected;
        list.ScrollIntoView(selected, ScrollIntoViewAlignment.Leading); await Rendered(); list.UpdateLayout();
        var before = list.ContainerFromItem(selected) as FrameworkElement ?? throw new InvalidOperationException("Application scroll anchor is not realized.");
        double top = before.TransformToVisual(list).TransformPoint(default).Y;
        source.SetSort(ApplicationGroupSort.Name, true); await Rendered(); list.UpdateLayout();
        var after = list.ContainerFromItem(selected) as FrameworkElement;
        Require(after is not null && Math.Abs(after.TransformToVisual(list).TransformPoint(default).Y - top) < 2
            && ReferenceEquals(list.SelectedItem, selected) && ReferenceEquals(choice.SelectedItem, selected), "Application sorting moved the viewport anchor or selected another group.");
        log("application-sort name=True;cpu=True;memory=True;processCount=True;liveMetrics=True;heldOrder=True;keyTie=True;selection=True;search=True;fullCaption=True;otherWindowCaption=True;scrollAnchor=True");
    }

    private static void CheckRow(ListView list, ApplicationGroupRow group, string stage, Action<string> log)
    {
        list.UpdateLayout();
        var container = list.ContainerFromItem(group) as ListViewItem;
        var text = container is null ? [] : TextBlocks(container).ToArray();
        log($"application-template {stage};items={list.Items.Count};container={container is not null};modelTitle={group.Title};modelSummary={group.Summary};text={Describe(text)}");
        Require(container is not null && HasText(text, group.Title) && HasText(text, group.CountText) && HasText(text, group.CpuText),
            "The actual application template lost visible title, counts or CPU text during " + stage + ".");
        var icons = container is null ? [] : Elements<ProcessIconView>(container).ToArray();
        Require(icons.Any(icon => ReferenceEquals(icon.Row, group.IconRow) && icon.Row?.Key == group.Key && icon.ActualWidth > 0 && icon.ActualHeight > 0),
            "The actual application template lost its exact process icon binding during " + stage + ".");
    }

    private static void CheckChoice(ComboBox choice, ApplicationGroupRow group, string stage, Action<string> log)
    {
        choice.UpdateLayout();
        var text = TextBlocks(choice).ToArray();
        log($"application-choice {stage};text={Describe(text)}");
        Require(ReferenceEquals(choice.SelectedItem, group) && HasText(text, group.Title),
            "The application choice lost its selected title during " + stage + ".");
    }

    private static bool HasText(IEnumerable<TextBlock> blocks, string expected) => expected.Length > 0
        && blocks.Any(block => block.Text == expected && block.Visibility == Visibility.Visible && block.ActualWidth > 0 && block.ActualHeight > 0);

    private static string Describe(IEnumerable<TextBlock> blocks) => string.Join("|", blocks.Select(block => $"{block.Text}@{block.ActualWidth:F0}x{block.ActualHeight:F0}"));

    private static IEnumerable<TextBlock> TextBlocks(DependencyObject root)
    {
        if (root is TextBlock text) yield return text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
            foreach (var child in TextBlocks(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static IEnumerable<T> Elements<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
            foreach (var child in Elements<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task Rendered()
    {
        var ready = new TaskCompletionSource();
        int frames = 0;
        void OnRendered(object? sender, object args) { if (++frames == 2) ready.TrySetResult(); }
        CompositionTarget.Rendering += OnRendered;
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= OnRendered; }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
