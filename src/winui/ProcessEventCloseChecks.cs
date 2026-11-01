using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    private static void ProcessEventCloseOwnership()
    {
        foreach (bool cancelMainFirst in new[] { false, true }) {
            var window = new ProcessEventsWindow(() => (0L, default(BootIdentifier), SampleState.Unavailable));
            try {
                var start = EventCloseButton(window.Content, "events-start");
                var clear = EventCloseButton(window.Content, "events-clear");
                Require(start.IsEnabled && clear.IsEnabled, "The idle event window did not expose enabled Start and Clear commands.");
                var main = window.StopForCloseAsync(fromMainWindow: true);
                var own = window.StopForCloseAsync();
                Require(main.IsCompletedSuccessfully && own.IsCompletedSuccessfully && main.GetAwaiter().GetResult() && own.GetAwaiter().GetResult(),
                    "Closing an event window without ETW unexpectedly waited for capture cleanup.");
                Require(!start.IsEnabled && !clear.IsEnabled, "Concurrent close requests left event commands enabled.");
                window.CancelClose(fromMainWindow: cancelMainFirst);
                Require(!start.IsEnabled && !clear.IsEnabled,
                    "Cancelling one close owner enabled Start or Clear while the other owner still held the event window.");
                window.CancelClose(fromMainWindow: !cancelMainFirst);
                Require(start.IsEnabled && clear.IsEnabled, "Cancelling both close owners did not restore the idle event commands.");
            } finally { window.CloseAfterStop(); }
        }
        App.Trace("Process event close ownership passed: actual idle-window commands, both cancellation orders, no ETW capture");
    }

    private static Button EventCloseButton(DependencyObject root, string automationId)
    {
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out var item)) {
            if (item is Button button && AutomationProperties.GetAutomationId(button) == automationId) return button;
            if (item is Panel panel) foreach (var child in panel.Children) pending.Push(child);
            else if (item is ContentControl { Content: DependencyObject content }) pending.Push(content);
            else if (item is Border { Child: { } borderChild }) pending.Push(borderChild);
        }
        throw new InvalidOperationException("The production event window is missing command " + automationId + ".");
    }
}
