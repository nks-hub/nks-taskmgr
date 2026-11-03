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
    internal static string? DumpCheckPath { get; } = Environment.GetCommandLineArgs()
        .FirstOrDefault(arg => arg.StartsWith("--check-dump=", StringComparison.Ordinal))?[13..];
    internal static string? ApplicationGroupsCheckPath { get; } = Environment.GetCommandLineArgs()
        .FirstOrDefault(arg => arg.StartsWith("--check-applications=", StringComparison.Ordinal))?[21..];
    internal static string? ProcessDetailsCheckPath { get; } = Environment.GetCommandLineArgs()
        .FirstOrDefault(arg => arg.StartsWith("--check-details=", StringComparison.Ordinal))?[16..];
    internal static bool ProcessDetailsUnhandledError { get; private set; }
    internal static bool ApplicationGroupsUnhandledError { get; private set; }
    internal static void Trace(string stage) { if ((SmokePath ?? EventsCheckPath ?? ActionsCheckPath ?? DumpCheckPath ?? ApplicationGroupsCheckPath ?? ProcessDetailsCheckPath) is { } path) File.AppendAllText(path, stage + Environment.NewLine); }
    private Window? window;

    public App()
    {
        SentryDiagnostics.Initialize();
        UnhandledException += (_, args) => {
            Trace(args.Exception.ToString());
            if (ApplicationGroupsCheckPath is not null) {
                ApplicationGroupsUnhandledError = true;
                Environment.ExitCode = 1;
                args.Handled = true;
            }
            if (ProcessDetailsCheckPath is not null) {
                ProcessDetailsUnhandledError = true;
                Environment.ExitCode = 1;
                args.Handled = true;
            }
            SentryDiagnostics.Capture(args.Exception, "Microsoft.UI.Xaml.UnhandledException", args.Handled);
        };
#if NKS_SENTRY
        try { InitializeComponent(); }
        catch (Exception error) {
            SentryDiagnostics.Capture(error, "Application.Startup", false);
            throw;
        }
#else
        InitializeComponent();
#endif
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if NKS_SENTRY
        try { LaunchApplication(); }
        catch (Exception error) {
            SentryDiagnostics.Capture(error, "Application.Launch", false);
            throw;
        }
#else
        LaunchApplication();
#endif
    }

    private void LaunchApplication()
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
        if (DumpCheckPath is { } dumpPath) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dumpPath))!);
            window = new ProcessDumpQaWindow(dumpPath); window.Activate(); return;
        }
        if (ApplicationGroupsCheckPath is { } applicationPath)
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(applicationPath))!);
        if (ProcessDetailsCheckPath is { } detailsPath)
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(detailsPath))!);
        Trace("Constructing window");
        var main = new MainWindow();
        window = main;
        main.Activate();
        if (ApplicationGroupsCheckPath is not null) main.BeginApplicationGroupChecks();
        if (ProcessDetailsCheckPath is { } processDetailsPath) main.BeginProcessDetailsChecks(processDetailsPath, () => ProcessDetailsUnhandledError);
    }
}
