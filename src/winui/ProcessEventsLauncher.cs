using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Button processEventsButton = Button("");
    private readonly ProcessTimeline processTimeline = new();
    private ProcessCapture? processCapture;
    private ProcessEventsWindow? processEventsWindow;
    private bool waitingForEventsStop, allowMainEventsClose;

    private void ConfigureProcessEvents()
    {
        processCapture = new ProcessCapture(DispatcherQueue, processTimeline, () => (model.ProcessTime, model.BootIdentity.Identifier, model.BootIdentity.State));
        if (RunningElevated) processCapture.Start();
        processEventsButton.Width = 35; processEventsButton.Padding = new Thickness(0);
        processEventsButton.Content = UiIcon.Create("events", 0x95a4ad, 19);
        AutomationProperties.SetName(processEventsButton, Loc.T("Spuštěné a ukončené procesy"));
        AutomationProperties.SetAutomationId(processEventsButton, "process-events");
        ToolTipService.SetToolTip(processEventsButton, Loc.T("Spuštěné a ukončené procesy"));
        processEventsButton.Click += (_, _) => {
            if (waitingForEventsStop || allowMainEventsClose) return;
            if (processEventsWindow is null) {
                var window = new ProcessEventsWindow(processTimeline, processCapture) {
                    RequestElevation = () => _ = OfferRestartElevatedAsync(Loc.T("Návratové kódy a velmi krátké procesy zachytí jen sledování ETW, které vyžaduje práva správce.")),
                    RevealProcess = key => {
                        if (model.Processes.FirstOrDefault(row => row.Key == key) is not { } row) return false;
                        RevealProcess(row); Activate(); return true;
                    }
                };
                processEventsWindow = window;
                window.Closed += (_, _) => { if (ReferenceEquals(processEventsWindow, window)) processEventsWindow = null; };
            }
            processEventsWindow.Activate();
        };
        AppWindow.Closing += OnMainEventsClosing;
        Closed += (_, _) => { allowMainEventsClose = true; processEventsWindow?.Close(); processCapture?.Dispose(); };
    }

    private void OnMainEventsClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowMainEventsClose || processCapture is not { Active: true } && liveCpuProfile is null) return;
        args.Cancel = true;
        RequestMainClose();
    }

    private void RequestMainClose() => DispatcherQueue.TryEnqueue(async () => await CloseMainAsync());

    private async Task CloseMainAsync()
    {
        if (waitingForEventsStop) return;
        var capture = processCapture;
        if (capture is not { Active: true } && liveCpuProfile is null) { Close(); return; }
        waitingForEventsStop = true; processEventsButton.IsEnabled = false;
        bool clean = capture is null || await capture.StopForCloseAsync();
        if (allowMainEventsClose) return;
        if (!clean) {
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = Loc.T("Zastavení a dočtení záznamu nebylo potvrzené"),
                PrimaryButtonText = Loc.T("Zavřít i tak"), CloseButtonText = Loc.T("Ponechat aplikaci otevřenou"),
                Content = Loc.T("Nepodařilo se potvrdit úklid vlastní relace ETW nebo dočíst její události. Relace může dál zaznamenávat a při zavření se mohou ztratit dosud nepřečtené události. Aplikaci lze ponechat otevřenou a zkusit zastavení znovu.") };
            Appearance.Dialog(dialog);
            try { clean = await dialog.ShowAsync() == ContentDialogResult.Primary; }
            catch (InvalidOperationException) { clean = false; }
        }
        if (clean) {
            if (!await FlushLiveCpuProfileAsync()) {
                if (Environment.ExitCode == 0) Environment.ExitCode = 2;
                System.Diagnostics.Debug.WriteLine("Tick CPU profile incomplete; persistence HRESULT=" + liveCpuProfile?.PersistenceError);
            }
            allowMainEventsClose = true; DispatcherQueue.TryEnqueue(() => { processEventsWindow?.Close(); Close(); });
        } else { waitingForEventsStop = false; capture?.Resume(); processEventsButton.IsEnabled = true; }
    }
}
