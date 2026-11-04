using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private void RefreshProcessOverview()
    {
        var cpu = model.Resources.FirstOrDefault(row => row.Kind == ResourceKind.Cpu);
        UpdateProcessOverview(ResourceKind.Cpu, "CPU", cpu?.Value ?? "Načítám…", cpu, "Celkové využití procesoru; otevřít graf procesoru.");

        var memory = model.Resources.FirstOrDefault(row => row.Kind == ResourceKind.Memory);
        string memoryValue = memory?.Value ?? "Načítám…";
        if (memory is { Stale: false, Capacity: > 0 } && memory.Primary.State == SampleState.Valid)
            memoryValue = Display.Number(memory.Primary.Value / 1073741824, "") + "/" + Display.Number(memory.Capacity / 1073741824, " GiB")
                + " · " + Display.Number(memory.Primary.Value / memory.Capacity * 100, " %");
        UpdateProcessOverview(ResourceKind.Memory, "RAM", memoryValue, memory, "Použitá a celková fyzická paměť; otevřít graf paměti.");

        var disks = model.Resources.Where(row => row.Kind == ResourceKind.Disk).ToArray();
        var disk = disks.Where(row => !row.Stale && row.Primary.State == SampleState.Valid)
            .OrderByDescending(row => row.Primary.Value).FirstOrDefault();
        UpdateProcessOverview(ResourceKind.Disk, "Disky · maximum", disk?.Value ?? (disks.Length + " zařízení"), disk,
            disk is null ? "Aktivita disků právě není dostupná." : "Nejvyšší aktuální aktivita: " + disk.Title + " · " + disk.Description);

        var network = model.Resources.Where(row => row.Kind == ResourceKind.Network && row.Status == "Připojeno")
            .OrderByDescending(row => Rate(row.Primary) + Rate(row.Secondary)).FirstOrDefault();
        UpdateProcessOverview(ResourceKind.Network, "Síť", network?.Value ?? "Žádné připojení", network,
            network is null ? "Žádný připojený adaptér v tomto pohledu."
                : "Největší aktuální provoz: " + network.Title + ". Příjem a odesílání tohoto adaptéru; hodnoty adaptérů se nesčítají.");
    }

    private static double Rate(Metric metric) => metric.State == SampleState.Valid && double.IsFinite(metric.Value) ? metric.Value : 0;

    private void UpdateProcessOverview(ResourceKind kind, string caption, string text, ResourceRow? target, string description)
    {
        if (!overviewValues.TryGetValue(kind, out var value)) {
            var button = Button("");
            button.Height = 28; button.Padding = new Thickness(6, 0, 6, 0); button.BorderThickness = new Thickness(0);
            button.Background = Display.Brush(0x171d21);
            var label = Label(caption, 11, 0x95a4ad);
            var metric = Label(text, 12, Display.ResourceColor(kind));
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(label); content.Children.Add(metric); button.Content = content;
            button.Click += (_, _) => {
                if (button.DataContext is not ResourceRow row || !model.Resources.Contains(row)) return;
                resourceList.SelectedItem = row; ShowView(DesktopView.Performance);
            };
            AutomationProperties.SetAutomationId(button, "process-overview-" + kind.ToString().ToLowerInvariant());
            value = (button, label, metric); overviewValues.Add(kind, value); overview.Children.Add(button);
        }
        if (value.Caption.Text != caption) value.Caption.Text = caption;
        if (value.Value.Text != text) value.Value.Text = text;
        if (!ReferenceEquals(value.Action.DataContext, target)) value.Action.DataContext = target;
        SetEnabled(value.Action, target is not null); SetToolTip(value.Action, description);
        string name = caption + ": " + text;
        if (AutomationProperties.GetName(value.Action) != name) AutomationProperties.SetName(value.Action, name);
    }

    // Property writes with an unchanged value still invalidate layout and notify automation, so refresh paths write only on change.
    private static void SetEnabled(Control control, bool value) { if (control.IsEnabled != value) control.IsEnabled = value; }
    private static void SetToolTip(DependencyObject target, string? tip) { if (!Equals(ToolTipService.GetToolTip(target), tip)) ToolTipService.SetToolTip(target, tip); }
}
