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

        list.UpdateLayout();
        var sizingRow = list.ContainerFromIndex(0) as FrameworkElement;
        Require(sizingRow is { IsLoaded: true, ActualHeight: > 0 } && double.IsFinite(sizingRow.ActualHeight)
            && list.ActualHeight > 0 && double.IsFinite(list.ActualHeight), "The application viewport sizing row is not realized.");
        int groupCount = Math.Max(20, checked((int)Math.Ceiling(list.ActualHeight / sizingRow!.ActualHeight) * 2 + 4));
        log(FormattableString.Invariant($"application-sort sizing;groups={groupCount};viewport={list.ActualHeight};rowHeight={sizingRow.ActualHeight}"));
        var rows = Enumerable.Range(0, groupCount).Select(index => new ProcessRow(new(process.Key.Boot, (uint)(100 + index), GenerationKind.CreationTime, (ulong)(1000 + index)))).ToArray();
        foreach (var row in rows) SetCpu(row, 1);
        source.SetSort(ApplicationGroupSort.Name, false);
        ApplyGroups(source, rows, rows.Select((row, index) => (row, "Application " + index.ToString("D2"))).ToArray());
        selected = source.Visible[rows.Length / 2]; source.Select(selected.Key); list.SelectedItem = choice.SelectedItem = selected;
        var scroll = Elements<ScrollViewer>(list).FirstOrDefault() ?? throw new InvalidOperationException("The application scroll viewer is not realized.");
        int viewEvents = 0, finalViewEvents = 0, viewChangingEvents = 0, viewSequence = 0; bool? lastIntermediate = null;
        string viewPhase = "positioning";
        void LogView(string kind, bool? intermediate) {
            if (++viewSequence <= 32)
                log(FormattableString.Invariant($"application-scroll phase={viewPhase};sequence={viewSequence};event={kind};intermediate={intermediate?.ToString() ?? "<not exposed>"};offset={scroll?.VerticalOffset};scrollable={scroll?.ScrollableHeight};viewport={scroll?.ViewportHeight};extent={scroll?.ExtentHeight}"));
        }
        void ViewChanging(object? sender, ScrollViewerViewChangingEventArgs args) { ++viewChangingEvents; LogView("changing", null); }
        void ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) {
            ++viewEvents; if (!args.IsIntermediate) ++finalViewEvents; lastIntermediate = args.IsIntermediate; LogView("changed", args.IsIntermediate);
        }
        (ApplicationGroupRow Row, FrameworkElement Container, int Index, double Top)? VisualAnchor() {
            if (!list.IsLoaded || !double.IsFinite(list.ActualWidth) || !double.IsFinite(list.ActualHeight)
                || list.ActualWidth <= 0 || list.ActualHeight <= 0 || list.ItemsPanelRoot is not { } panel) return null;
            (ApplicationGroupRow Row, FrameworkElement Container, int Index, double Top)? first = null;
            foreach (var child in panel.Children) {
                if (child is not FrameworkElement { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } item) continue;
                int index = list.IndexFromContainer(item);
                if (index < 0 || index >= list.Items.Count || list.ItemFromContainer(item) is not ApplicationGroupRow row
                    || !ReferenceEquals(row, list.Items[index])) continue;
                var bounds = item.TransformToVisual(list).TransformBounds(new Windows.Foundation.Rect(0, 0, item.ActualWidth, item.ActualHeight));
                if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom)
                    || bounds.Width <= 0 || bounds.Height <= 0 || bounds.Right <= 0 || bounds.Bottom <= 0
                    || bounds.Left >= list.ActualWidth || bounds.Top >= list.ActualHeight) continue;
                bool visible = true;
                for (DependencyObject? ancestor = item; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                    if (ancestor is UIElement { Visibility: Visibility.Collapsed }) { visible = false; break; }
                if (!visible || first is { } previous && (bounds.Top > previous.Top || bounds.Top == previous.Top && index >= previous.Index)) continue;
                first = (row, item, index, bounds.Top);
            }
            return first;
        }
        void LogAnchor(string phase) {
            var panel = list.ItemsPanelRoot; string nativePanel = "<null>";
            if (panel is not null) {
                try { nativePanel = new WinRT.IInspectable(((WinRT.IWinRTObject)panel).NativeObject).GetRuntimeClassName(false); }
                catch (Exception error) { nativePanel = $"error:{error.GetType().Name}:{error.HResult:X8}"; }
            }
            string firstVisible = VisualAnchor() is { } visibleAnchor
                ? $"index={visibleAnchor.Index},key={visibleAnchor.Row.Key},y={visibleAnchor.Top},height={visibleAnchor.Container.ActualHeight}" : "<none>";
            var container = list.ContainerFromItem(selected) as FrameworkElement;
            log(FormattableString.Invariant($"application-anchor phase={phase};selected={selected.Key};selectedIndex={source.Visible.IndexOf(selected)};modelSelection={source.Selected?.Key};listSelection={(list.SelectedItem as ApplicationGroupRow)?.Key};choiceSelection={(choice.SelectedItem as ApplicationGroupRow)?.Key};modelRef={ReferenceEquals(source.Selected, selected)};listRef={ReferenceEquals(list.SelectedItem, selected)};choiceRef={ReferenceEquals(choice.SelectedItem, selected)};listType={list.SelectedItem?.GetType().FullName};choiceType={choice.SelectedItem?.GetType().FullName};container={container is not null};containerLoaded={container?.IsLoaded};selectedY={container?.TransformToVisual(list).TransformPoint(default).Y};selectedHeight={container?.ActualHeight};listWidth={list.ActualWidth};listHeight={list.ActualHeight};scrollNull={scroll is null};offset={scroll?.VerticalOffset};scrollable={scroll?.ScrollableHeight};viewport={scroll?.ViewportHeight};extent={scroll?.ExtentHeight};panelType={panel?.GetType().FullName};panelNative={nativePanel};panelCast={panel is ItemsStackPanel};castFirst={(panel as ItemsStackPanel)?.FirstVisibleIndex};visualFirst={firstVisible};viewChangingEvents={viewChangingEvents};viewEvents={viewEvents};finalViewEvents={finalViewEvents};lastIntermediate={lastIntermediate};viewSequence={viewSequence};droppedViewLogs={Math.Max(0, viewSequence - 32)}"));
        }
        scroll.ViewChanging += ViewChanging; scroll.ViewChanged += ViewChanged;
        try {
            await ScrollSettled(list, scroll, () => list.ScrollIntoView(selected, ScrollIntoViewAlignment.Leading));
            var before = list.ContainerFromItem(selected) as FrameworkElement ?? throw new InvalidOperationException("Application scroll anchor is not realized.");
            double top = before.TransformToVisual(list).TransformPoint(default).Y;
            Require(VisualAnchor() is { } initialAnchor && ReferenceEquals(initialAnchor.Row, selected),
                "The selected application fixture is not the actual first visible group.");
            LogAnchor("before"); viewEvents = finalViewEvents = viewChangingEvents = viewSequence = 0; lastIntermediate = null; viewPhase = "sorting";
            await ScrollSettled(list, scroll, () => source.SetSort(ApplicationGroupSort.Name, true));
            var after = list.ContainerFromItem(selected) as FrameworkElement;
            LogAnchor("after");
            Require(after is not null && Math.Abs(after.TransformToVisual(list).TransformPoint(default).Y - top) < 2
                && ReferenceEquals(list.SelectedItem, selected) && ReferenceEquals(choice.SelectedItem, selected), "Application sorting moved the viewport anchor or selected another group.");
            Require(ReferenceEquals(source.Selected, selected) && VisualAnchor() is { } selectedAnchor && ReferenceEquals(selectedAnchor.Row, selected),
                "Sorting replaced the selected first visible application anchor.");

            viewPhase = "last-reset"; viewEvents = finalViewEvents = viewChangingEvents = viewSequence = 0; lastIntermediate = null;
            await ScrollSettled(list, scroll, () => source.SetSort(ApplicationGroupSort.Name, false));
            selected = source.Visible[^1];
            viewPhase = "last-positioning"; viewEvents = finalViewEvents = viewChangingEvents = viewSequence = 0; lastIntermediate = null;
            await ScrollSettled(list, scroll, () => {
                source.Select(selected.Key); list.SelectedItem = choice.SelectedItem = selected;
                list.ScrollIntoView(selected, ScrollIntoViewAlignment.Leading);
            });
            var endAnchor = VisualAnchor() ?? throw new InvalidOperationException("The clamped application viewport has no verified visible group.");
            Require(scroll.ScrollableHeight > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1
                && !ReferenceEquals(endAnchor.Row, selected), "The last-selected application fixture did not reach its distinct clamped viewport anchor.");
            var endAnchorKey = endAnchor.Row.Key;
            LogAnchor("last-before"); viewEvents = finalViewEvents = viewChangingEvents = viewSequence = 0; lastIntermediate = null; viewPhase = "last-sorting";
            await ScrollSettled(list, scroll, () => source.SetSort(ApplicationGroupSort.Name, true));
            LogAnchor("last-after");
            var movedAnchor = list.ContainerFromItem(endAnchor.Row) as FrameworkElement
                ?? throw new InvalidOperationException("Sorting lost the actual clamped viewport anchor.");
            double contentTop = scroll.VerticalOffset + movedAnchor.TransformToVisual(list).TransformPoint(default).Y;
            double requiredOffset = contentTop - endAnchor.Top;
            Require(double.IsFinite(requiredOffset) && requiredOffset >= 0 && requiredOffset <= scroll.ScrollableHeight,
                "The clamped-anchor fixture cannot preserve its original position within the new scroll range.");
            var finalAnchor = VisualAnchor();
            Require(finalAnchor is { } actualAnchor && ReferenceEquals(actualAnchor.Row, endAnchor.Row) && actualAnchor.Row.Key == endAnchorKey
                && Math.Abs(actualAnchor.Top - endAnchor.Top) < 2, "Sorting moved the true viewport anchor while the last group stayed selected.");
            Require(ReferenceEquals(source.Selected, selected) && ReferenceEquals(list.SelectedItem, selected) && ReferenceEquals(choice.SelectedItem, selected),
                "Restoring the clamped viewport anchor changed the last selected group identity.");
            log("application-sort endAnchor=True;lastSelected=True;clamped=True;differentAnchor=True;selection=True");
        } finally { scroll.ViewChanging -= ViewChanging; scroll.ViewChanged -= ViewChanged; }
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

    private static async Task ScrollSettled(ListView list, ScrollViewer scroll, Action action)
    {
        var ready = new TaskCompletionSource();
        bool changing = false;
        int stableFrames = 0, version = 0, lastVersion = -1;
        double lastOffset = double.NaN, lastViewport = double.NaN, lastExtent = double.NaN;
        void Changing(object? sender, ScrollViewerViewChangingEventArgs args) { changing = true; ++version; stableFrames = 0; }
        void Changed(object? sender, ScrollViewerViewChangedEventArgs args) { changing = args.IsIntermediate; ++version; stableFrames = 0; }
        void Frame(object? sender, object args) {
            bool stable = !changing && lastVersion == version && scroll.VerticalOffset == lastOffset
                && scroll.ViewportHeight == lastViewport && scroll.ExtentHeight == lastExtent;
            stableFrames = stable ? stableFrames + 1 : 0;
            lastVersion = version; lastOffset = scroll.VerticalOffset; lastViewport = scroll.ViewportHeight; lastExtent = scroll.ExtentHeight;
            if (stableFrames >= 2) ready.TrySetResult();
        }
        scroll.ViewChanging += Changing; scroll.ViewChanged += Changed; CompositionTarget.Rendering += Frame;
        try {
            action(); list.UpdateLayout();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            list.UpdateLayout();
        } finally {
            scroll.ViewChanging -= Changing; scroll.ViewChanged -= Changed; CompositionTarget.Rendering -= Frame;
        }
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
