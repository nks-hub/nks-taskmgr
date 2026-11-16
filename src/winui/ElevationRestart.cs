using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

/// <summary>
/// Restarts the whole application with an administrator token through the normal UAC prompt. The single-instance
/// name differs per elevation level, so the elevated copy starts on its own and this copy closes after it launched.
/// </summary>
public sealed partial class MainWindow
{
    internal static bool RunningElevated { get; } = LaunchArguments.IsElevated();

    private string CurrentViewArgument() => view switch {
        DesktopView.Services => "services", DesktopView.Applications => "applications", DesktopView.Processes => "processes",
        DesktopView.Users => "users", DesktopView.Startup => "startup", DesktopView.Hardware => "hardware", _ => "performance"
    };

    /// <summary>Starts an elevated copy in the current view and closes this one; false when the UAC prompt was declined or failed.</summary>
    internal bool RestartElevated()
    {
        if (RunningElevated || Environment.ProcessPath is not { } path) return false;
        try {
            Process.Start(new ProcessStartInfo(path, "--view=" + CurrentViewArgument()) { UseShellExecute = true, Verb = "runas" })?.Dispose();
        } catch (Win32Exception error) when (error.NativeErrorCode == 1223) {
            return false;
        } catch (Win32Exception error) {
            _ = ShowElevationFailureAsync(error.Message);
            return false;
        }
        Close();
        return true;
    }

    /// <summary>Explains why a feature needs an administrator and offers the restart; the user decides in the dialog.</summary>
    internal async Task<bool> OfferRestartElevatedAsync(string reason)
    {
        if (RunningElevated) return false;
        var dialog = Appearance.Dialog(new ContentDialog {
            Title = Loc.T("Vyžaduje oprávnění správce"), XamlRoot = root.XamlRoot,
            Content = ServiceActionText(reason + "\n" + Loc.T("NKS Task Manager se zavře a spustí znovu jako správce ve stejném pohledu. Windows zobrazí výzvu UAC."), 0xe9eef0),
            PrimaryButtonText = Loc.T("Spustit jako správce"), CloseButtonText = Loc.T("Zrušit"), DefaultButton = ContentDialogButton.Primary
        });
        return await dialog.ShowAsync() == ContentDialogResult.Primary && RestartElevated();
    }

    private async Task ShowElevationFailureAsync(string message)
    {
        await Appearance.Dialog(new ContentDialog {
            Title = Loc.T("Spuštění jako správce selhalo"), XamlRoot = root.XamlRoot, Content = ServiceActionText(message, 0xe9eef0), CloseButtonText = Loc.T("Zavřít")
        }).ShowAsync();
    }

    private void AddElevationMenu(MenuFlyout menu)
    {
        menu.Items.Add(new MenuFlyoutSeparator());
        var always = new ToggleMenuFlyoutItem { Text = Loc.T("Spouštět vždy jako správce"), IsChecked = ElevationPreference.Always };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(always, "always-elevated");
        ToolTipService.SetToolTip(always, Loc.T("Při každém spuštění, i přes Ctrl+Shift+Esc, se Windows zeptá na práva správce (výzva UAC). Bez nich chybí návratové kódy a velmi krátké procesy, síť není rozdělená na příjem a odeslání a podrobnosti cizích procesů nejsou dostupné."));
        always.Click += (_, _) => {
            if (!ElevationPreference.Select(always.IsChecked)) { always.IsChecked = ElevationPreference.Always; footer.Text = Loc.T("Volbu se nepodařilo uložit."); return; }
            footer.Text = always.IsChecked && !RunningElevated ? Loc.T("Od příštího spuštění poběží NKS jako správce.") : "";
        };
        menu.Items.Add(always);
        if (RunningElevated) return;
        var item = new MenuFlyoutItem { Text = Loc.T("Spustit znovu jako správce"), Icon = new FontIcon { Glyph = "" } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "restart-elevated");
        item.Click += (_, _) => RestartElevated();
        menu.Items.Add(item);
    }
}

/// <summary>The per-user choice to start every launch elevated; a standard token then relaunches itself through UAC before any window opens.</summary>
internal static class ElevationPreference
{
    private const string Marker = "--elevation-requested";
    private static readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NksTaskMgr", "elevation.txt");
    internal static bool Always { get; private set; } = Read();

    internal static bool Select(bool always)
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, always ? "always" : "never");
            Always = always; return true;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Read()
    {
        try { return File.Exists(path) && File.ReadAllText(path).Trim() == "always"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Arguments for the elevated relaunch, or null when this launch stays as it is. The marker stops a loop when UAC
    /// is off for a standard user and the relaunch comes back without elevation.
    /// </summary>
    internal static string[]? RelaunchArguments(bool always, bool elevated, string[] args) =>
        !always || elevated || args.Contains(Marker) || LaunchArguments.Parse(args).Independent ? null : [.. args.Skip(1), Marker];

    /// <summary>True when an elevated copy was started and this process should exit; a declined UAC prompt keeps this launch.</summary>
    internal static bool TryRelaunch()
    {
        if (RelaunchArguments(Always, MainWindow.RunningElevated, LaunchArguments.Current) is not { } arguments || Environment.ProcessPath is not { } exe) return false;
        var start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try { Process.Start(start)?.Dispose(); return true; }
        catch (Win32Exception) { return false; }
    }

    internal static void Check()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        Require(RelaunchArguments(true, false, ["gui"]) is [Marker], "An always-elevated launch did not relaunch.");
        Require(RelaunchArguments(true, false, ["gui", "--view=processes"]) is ["--view=processes", Marker], "The relaunch lost the requested view.");
        Require(RelaunchArguments(false, false, ["gui"]) is null && RelaunchArguments(true, true, ["gui"]) is null, "A launch relaunched without the choice or while elevated.");
        Require(RelaunchArguments(true, false, ["gui", Marker]) is null, "A relaunch that came back without elevation relaunched again.");
        Require(RelaunchArguments(true, false, ["gui", "--smoke=x"]) is null, "A QA launch relaunched elevated.");
        Require(LaunchArguments.Parse(["gui", Marker]) == new LaunchRequest(false, null), "The relaunch marker changed the launch.");
    }
}
