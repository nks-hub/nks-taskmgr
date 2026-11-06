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
        if (RunningElevated) return;
        var item = new MenuFlyoutItem { Text = Loc.T("Spustit znovu jako správce"), Icon = new FontIcon { Glyph = "" } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "restart-elevated");
        item.Click += (_, _) => RestartElevated();
        menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(item);
    }
}
