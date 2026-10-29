using Microsoft.UI.Xaml;

namespace NksTaskMgr.WinUI;

public partial class App : Application
{
    internal static string? SmokePath { get; } = Environment.GetCommandLineArgs()
        .FirstOrDefault(arg => arg.StartsWith("--smoke=", StringComparison.Ordinal))?[8..];
    internal static void Trace(string stage) { if (SmokePath is not null) File.AppendAllText(SmokePath, stage + Environment.NewLine); }
    private Window? window;

    public App()
    {
        UnhandledException += (_, args) => Trace(args.Exception.ToString());
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Trace("Constructing window");
        window = new MainWindow();
        window.Activate();
    }
}
