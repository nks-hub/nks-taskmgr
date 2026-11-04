using Microsoft.UI.Xaml;

namespace NksTaskMgr.WinUI;

public partial class App : Application
{
    internal static string? SmokePath { get; } = LaunchArguments.Value(LaunchArguments.Current, "--smoke=");
    internal static string? SortBenchmarkPath { get; } = LaunchArguments.Value(LaunchArguments.Current, "--bench-sort=");
    internal static string? EventsCheckPath { get; } = LaunchArguments.Value(LaunchArguments.Current, "--check-events=");
    internal static string? ActionsCheckPath { get; } = LaunchArguments.Value(LaunchArguments.Current, "--check-actions=");
    internal static string? DumpCheckPath { get; } = LaunchArguments.Value(LaunchArguments.Current, "--check-dump=");
    internal static string? ApplicationGroupsCheckPath { get; } = LaunchArguments.Value(LaunchArguments.Current, "--check-applications=");
    internal static string? ProcessDetailsCheckPath { get; } = LaunchArguments.Value(LaunchArguments.Current, "--check-details=");
    internal static bool ProcessDetailsUnhandledError { get; private set; }
    internal static bool ApplicationGroupsUnhandledError { get; private set; }
    internal static void Trace(string stage) { if ((SmokePath ?? EventsCheckPath ?? ActionsCheckPath ?? DumpCheckPath ?? ApplicationGroupsCheckPath ?? ProcessDetailsCheckPath) is { } path) File.AppendAllText(path, stage + Environment.NewLine); }
    private Window? window;

    /// <summary>
    /// Appends an unhandled UI-thread exception to a local, size-bounded log; nothing is sent anywhere.
    /// Without it a release build without Sentry leaves only a minidump that lacks the managed exception.
    /// </summary>
    private static void RecordCrash(Exception error)
    {
        try {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NksTaskMgr");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "crash.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 256 * 1024) File.Move(path, path + ".1", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion} {error}{Environment.NewLine}{Environment.NewLine}");
        } catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
    }

    public App()
    {
        SentryDiagnostics.Initialize();
        UnhandledException += (_, args) => {
            Trace(args.Exception.ToString());
            RecordCrash(args.Exception);
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
        bool shared = !LaunchArguments.Parse(LaunchArguments.Current).Independent;
        if (shared && !SingleInstance.TryBecomePrimary()) Environment.Exit(0);
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
        if (shared) {
            SingleInstance.Listen(main.DispatcherQueue, main.ActivateExisting);
            main.Closed += (_, _) => SingleInstance.Stop();
        }
        if (ApplicationGroupsCheckPath is not null) main.BeginApplicationGroupChecks();
        if (ProcessDetailsCheckPath is { } processDetailsPath) main.BeginProcessDetailsChecks(processDetailsPath, () => ProcessDetailsUnhandledError);
    }
}
