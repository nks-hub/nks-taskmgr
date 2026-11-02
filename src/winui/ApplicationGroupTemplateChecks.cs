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
            log("application-template aot=True;initial=True;titleUpdate=True;summaryUpdate=True;rebound=True;choice=True");
        } finally {
            list.ItemsSource = originalList; choice.ItemsSource = originalChoice;
            list.SelectedItem = originalListSelection; choice.SelectedItem = originalChoiceSelection;
            list.Visibility = listVisibility; choice.Visibility = choiceVisibility;
        }
    }

    private static void Apply(ApplicationGroupModel source, ProcessRow process, string title)
    {
        source.Apply(new ProcessDetailsSnapshot(ProcessDetailsCommand.Applications, ProcessDetailsState.Ok, 0, 0,
            ProcessArchitecture.X64, false, [], [], null, DateTimeOffset.UtcNow) {
            Applications = [new ProcessApplicationInfo(1, process.Key, null, title, "", "", SampleState.Unavailable, SampleState.Unavailable)]
        });
        source.Refresh([process], "");
    }

    private static void SetCpu(ProcessRow process, double cpu) => process.Update(new ProcessInfo {
        Pid = process.Key.Pid, Generation = process.Key.Generation, GenerationKind = process.Key.Kind,
        CpuPercent = new Metric { Value = cpu, State = SampleState.Valid, Source = MetricSource.NativeProcess }
    });

    private static void CheckRow(ListView list, ApplicationGroupRow group, string stage, Action<string> log)
    {
        list.UpdateLayout();
        var container = list.ContainerFromItem(group) as ListViewItem;
        var text = container is null ? [] : TextBlocks(container).ToArray();
        log($"application-template {stage};items={list.Items.Count};container={container is not null};modelTitle={group.Title};modelSummary={group.Summary};text={Describe(text)}");
        Require(container is not null && HasText(text, group.Title) && HasText(text, group.Summary),
            "The actual application template lost visible title or summary text during " + stage + ".");
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
