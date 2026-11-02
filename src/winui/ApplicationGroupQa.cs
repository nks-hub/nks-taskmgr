namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    internal async void BeginApplicationGroupChecks()
    {
        try {
            model.SetPaused(true);
            SelectProcessMode(ProcessMode.Applications);
            ShowView(DesktopView.Processes);
            await ApplicationGroupTemplateChecks.RunAsync(applicationGroups, applicationChoice, App.Trace);
            if (App.ApplicationGroupsUnhandledError) throw new InvalidOperationException("Application group rendering raised an unhandled error.");
            App.Trace("Application group checks passed");
        } catch (Exception error) {
            App.Trace(error.ToString());
            Environment.ExitCode = 1;
        } finally { Close(); }
    }
}
