using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Button processEventsButton = Button("");
    private ProcessEventsWindow? processEventsWindow;
    private bool waitingForEventsStop, allowMainEventsClose;

    private void ConfigureProcessEvents()
    {
        processEventsButton.Width = 35; processEventsButton.Padding = new Thickness(0);
        processEventsButton.Content = UiIcon.Create("events", 0x95a4ad, 19);
        AutomationProperties.SetName(processEventsButton, "Události procesů ETW");
        AutomationProperties.SetAutomationId(processEventsButton, "process-events");
        ToolTipService.SetToolTip(processEventsButton, "Události vzniku a ukončení procesů");
        processEventsButton.Click += (_, _) => {
            if (waitingForEventsStop || allowMainEventsClose) return;
            if (processEventsWindow is null) {
                var window = new ProcessEventsWindow(() => (model.ProcessTime, model.BootIdentity.Identifier, model.BootIdentity.State));
                processEventsWindow = window;
                window.Closed += (_, _) => { if (ReferenceEquals(processEventsWindow, window)) processEventsWindow = null; };
            }
            processEventsWindow.Activate();
        };
        AppWindow.Closing += OnMainEventsClosing;
        Closed += (_, _) => { allowMainEventsClose = true; processEventsWindow?.CloseAfterStop(); };
    }

    private async void OnMainEventsClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowMainEventsClose || processEventsWindow is not { } window) return;
        args.Cancel = true;
        if (waitingForEventsStop) return;
        waitingForEventsStop = true; processEventsButton.IsEnabled = false;
        bool clean = await window.StopForCloseAsync(fromMainWindow: true);
        if (allowMainEventsClose) return;
        if (!clean) {
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "Zastavení a dočtení záznamu nebylo potvrzené",
                PrimaryButtonText = "Zavřít i tak", CloseButtonText = "Ponechat aplikaci otevřenou",
                Content = "Nepodařilo se potvrdit úklid vlastní relace ETW nebo dočíst její události. Relace může dál zaznamenávat a při zavření se mohou ztratit dosud nepřečtené události. Aplikaci lze ponechat otevřenou a zkusit zastavení znovu." };
            Appearance.Dialog(dialog);
            try { clean = await dialog.ShowAsync() == ContentDialogResult.Primary; }
            catch (InvalidOperationException) { clean = false; }
        }
        if (clean) { allowMainEventsClose = true; DispatcherQueue.TryEnqueue(() => { window.CloseAfterStop(); Close(); }); }
        else { waitingForEventsStop = false; window.CancelClose(fromMainWindow: true); processEventsButton.IsEnabled = true; }
    }
}
