using Microsoft.UI.Xaml;

namespace NksTaskMgr.WinUI;

public partial class App : Application
{
    internal static string? SmokePath { get; } = Environment.GetCommandLineArgs()
        .FirstOrDefault(arg => arg.StartsWith("--smoke=", StringComparison.Ordinal))?[8..];
    internal static string? SortBenchmarkPath { get; } = Environment.GetCommandLineArgs()
        .FirstOrDefault(arg => arg.StartsWith("--bench-sort=", StringComparison.Ordinal))?[13..];
    internal static string? EventsCheckPath { get; } = Environment.GetCommandLineArgs()
        .FirstOrDefault(arg => arg.StartsWith("--check-events=", StringComparison.Ordinal))?[15..];
    internal static string? ActionsCheckPath { get; } = Environment.GetCommandLineArgs()
        .FirstOrDefault(arg => arg.StartsWith("--check-actions=", StringComparison.Ordinal))?[16..];
    internal static void Trace(string stage) { if ((SmokePath ?? EventsCheckPath ?? ActionsCheckPath) is { } path) File.AppendAllText(path, stage + Environment.NewLine); }
    private Window? window;

    public App()
    {
        UnhandledException += (_, args) => Trace(args.Exception.ToString());
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (EventsCheckPath is { } path) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var events = new ProcessEventsWindow(() => (0L, default(NksTaskMgr.Native.BootIdentifier), NksTaskMgr.Native.SampleState.Unavailable));
            window = events; events.BeginUiChecks(path); events.Activate(); return;
        }
        if (ActionsCheckPath is { } actionsPath) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(actionsPath))!);
            window = new ProcessActionsUiChecks(actionsPath); window.Activate(); return;
        }
        Trace("Constructing window");
        window = new MainWindow();
        window.Activate();
    }
}
