using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private UpdateWindow? updateWindow;

    private void ConfigureUpdates()
    {
        var button = Button("Aktualizace");
        button.FontSize = 11; button.Height = 29;
        button.Padding = new Thickness(4, 0, 4, 0);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Bottom;
        button.Margin = new Thickness(8, 0, 8, 52);
        AutomationProperties.SetName(button, "Aktualizace a návrat předchozí verze");
        AutomationProperties.SetAutomationId(button, "updates-open");
        ToolTipService.SetToolTip(button, "Verze aplikace a možnosti aktualizace");
        button.Click += async (_, _) => {
            try {
                if (updateWindow is null) {
                    var window = new UpdateWindow(RequestMainClose); updateWindow = window;
                    window.Closed += (_, _) => { if (ReferenceEquals(updateWindow, window)) updateWindow = null; };
                }
                updateWindow.ShowOwned(this);
            } catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or InvalidOperationException) {
                updateWindow?.Close(); updateWindow = null;
                await new ContentDialog { Title = "Aktualizace nelze otevřít", Content = error.Message,
                    CloseButtonText = "Zavřít", XamlRoot = root.XamlRoot }.ShowAsync();
            }
        };
        sidebar.Children.Add(button);
        Closed += (_, _) => updateWindow?.Close();
    }
}
