using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private UpdateWindow? updateWindow;
    private readonly Button updatesButton = Button(Loc.T("Aktualizace"));

    private void ConfigureUpdates()
    {
        var button = updatesButton;
        button.FontSize = 11; button.Height = 29;
        button.Padding = new Thickness(4, 0, 4, 0);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Bottom;
        button.Margin = new Thickness(8, 0, 8, 52);
        AutomationProperties.SetName(button, Loc.T("Aktualizace a návrat předchozí verze"));
        AutomationProperties.SetAutomationId(button, "updates-open");
        ToolTipService.SetToolTip(button, Loc.T("Verze aplikace a možnosti aktualizace"));
        button.Click += async (_, _) => {
            try {
                if (updateWindow is null) {
                    var window = new UpdateWindow(RequestMainClose); updateWindow = window;
                    window.Closed += (_, _) => { if (ReferenceEquals(updateWindow, window)) updateWindow = null; };
                }
                updateWindow.ShowOwned(this);
            } catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or InvalidOperationException) {
                updateWindow?.Close(); updateWindow = null;
                await new ContentDialog { Title = Loc.T("Aktualizace nelze otevřít"), Content = error.Message,
                    CloseButtonText = Loc.T("Zavřít"), XamlRoot = root.XamlRoot }.ShowAsync();
            }
        };
        sidebar.Children.Add(button);
        Closed += (_, _) => updateWindow?.Close();
        if (!LaunchArguments.Parse(LaunchArguments.Current).Independent) _ = HighlightNewReleaseAsync();
    }

    /// <summary>One GitHub check shortly after start; a newer release turns the Updates button into an accent button.</summary>
    private async Task HighlightNewReleaseAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        try {
            if (OnlineUpdate.CurrentVersion() is not { } current || await OnlineUpdate.FindNewerAsync(current, CancellationToken.None) is not { } release) return;
            string text = Loc.F("K dispozici je verze {0}.", release.Version);
            ToolTipService.SetToolTip(updatesButton, text); AutomationProperties.SetHelpText(updatesButton, text);
            updatesButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        } catch (Exception error) when (error is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
            or InvalidDataException or KeyNotFoundException or InvalidOperationException or IOException or UriFormatException) {
            // Offline or rate-limited: the Updates window checks again when opened.
        }
    }
}
