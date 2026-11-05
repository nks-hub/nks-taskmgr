using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private Task? smokeKeyboard;

    /// <summary>Moves keyboard focus with Tab through the Processes view and requires a closed cycle over every main control.</summary>
    private async Task CheckKeyboardTraversalAsync()
    {
        var originalView = view;
        bool Inside(DependencyObject? node, DependencyObject target)
        {
            for (; node is not null; node = VisualTreeHelper.GetParent(node)) if (ReferenceEquals(node, target)) return true;
            return false;
        }
        try {
            ShowView(DesktopView.Processes);
            await NextRenderedFrame(); await NextRenderedFrame(); root.UpdateLayout();
            if (!processesNav.Focus(FocusState.Keyboard)) throw new InvalidOperationException("Keyboard check could not focus the Processes navigation button.");
            var options = new FindNextElementOptions { SearchRoot = root.XamlRoot.Content };
            var required = new List<(string Name, DependencyObject Target)> {
                ("Procesy", processesNav), ("Aplikace", applicationNav), ("Výkon", performanceNav), ("Služby", servicesNav),
                ("Uživatelé", usersNav), ("Sestava", hardwareNav), ("Po spuštění", startupNav), ("hledání", search),
                ("interval", interval), ("pauza", pause), ("tabulka procesů", processList) };
            var reached = new HashSet<string>(); var visited = new HashSet<DependencyObject>(); int steps = 0, wraps = 0; bool closed = false;
            while (steps < 300) {
                var result = await FocusManager.TryMoveFocusAsync(FocusNavigationDirection.Next, options);
                ++steps;
                if (!result.Succeeded) {
                    // The window wraps Tab at the end of its sequence; the focus API stops there instead.
                    if (++wraps > 1 || FocusManager.FindFirstFocusableElement(root.XamlRoot.Content) is not UIElement first
                        || !(await FocusManager.TryFocusAsync(first, FocusState.Keyboard)).Succeeded)
                        throw new InvalidOperationException($"Tab stopped after {steps} steps without wrapping to the first control.");
                }
                if (FocusManager.GetFocusedElement(root.XamlRoot) is not DependencyObject focused) throw new InvalidOperationException("Tab left focus nowhere.");
                if (ReferenceEquals(focused, processesNav)) { closed = true; reached.Add("Procesy"); break; }
                if (!visited.Add(focused)) throw new InvalidOperationException($"Tab revisited {focused.GetType().Name} '{AutomationProperties.GetName(focused)}' before closing the cycle.");
                if (focused is FrameworkElement { ActualWidth: <= 0 } or UIElement { Visibility: Visibility.Collapsed })
                    throw new InvalidOperationException($"Tab focused an invisible {focused.GetType().Name}.");
                foreach (var item in required) if (Inside(focused, item.Target)) reached.Add(item.Name);
            }
            var missing = required.Select(item => item.Name).Where(name => !reached.Contains(name)).ToArray();
            if (!closed || missing.Length > 0)
                throw new InvalidOperationException($"Tab cycle closed={closed} after {steps} steps; unreachable: {string.Join(", ", missing)}.");
            App.Trace($"Keyboard traversal passed: Tab cycle of {steps} stops reaches {reached.Count} main controls and returns to the start; reached: {string.Join(", ", reached)}");
        } finally {
            ShowView(originalView);
        }
    }
}
