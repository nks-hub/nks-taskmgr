namespace NksTaskMgr.WinUI;

public partial class App
{
    internal static string? GapDiagnosticsPath { get; } = ParseGapDiagnosticsPath(LaunchArguments.Current);

    internal static string? ParseGapDiagnosticsPath(ReadOnlySpan<string> arguments)
    {
        const string prefix = "--diagnose-gaps=";
        string? path = null;
        foreach (string argument in arguments) {
            if (!argument.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (path is not null) throw new ArgumentException("Specify --diagnose-gaps only once.");
            if (argument.Length == prefix.Length) throw new ArgumentException("--diagnose-gaps requires an output file.");
            path = Path.GetFullPath(argument[prefix.Length..]);
        }
        return path;
    }
}

public sealed partial class MainWindow
{
    private bool gapDiagnosticsClosing;

    private void ConfigureGapDiagnostics()
    {
        if (App.GapDiagnosticsPath is not { } path) return;
        model.BeginGapDiagnostics(path);
        Closed += (_, _) => FinishGapDiagnostics("windowClosed");
    }

    private void RecordGapDiagnosticTick()
    {
        if (App.GapDiagnosticsPath is null || gapDiagnosticsClosing) return;
        if (!model.RecordGapDiagnosticTick((int)view, cpuMode, rangeSeconds, selectedResource?.Id, IsIconic(windowHandle))) return;
        gapDiagnosticsClosing = true;
        FinishGapDiagnostics("durationElapsed");
        RequestMainClose();
    }

    private void RecordGapDiagnosticError(Exception error) => model.RecordGapDiagnosticError(error);

    private void FinishGapDiagnostics(string reason)
    {
        try { model.FinishGapDiagnostics(reason); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
            Environment.ExitCode = 1;
            System.Diagnostics.Debug.WriteLine("Gap diagnostics could not be persisted: " + error);
        }
    }
}
