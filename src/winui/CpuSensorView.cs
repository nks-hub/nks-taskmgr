using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MonitorModel
{
    public bool CpuSensorsEnabled { get; private set; }
    private string cpuSensorError = string.Empty;

    public void SetCpuSensors(bool enabled)
    {
        if (CpuSensorsEnabled == enabled && cpuSensorError.Length == 0) return;
        Hardware.Remove("cpu-sensors");
        CpuSensorsEnabled = enabled;
        cpuSensorError = string.Empty;
        try { monitor.SetCpuSensors(enabled); }
        catch (EntryPointNotFoundException) {
            CpuSensorsEnabled = false;
            cpuSensorError = "Tato knihovna čtení CPU čidel nepodporuje";
        }
        catch (NativeMonitorException) {
            CpuSensorsEnabled = false;
            cpuSensorError = "Změnu čtení CPU čidel knihovna nepotvrdila";
        }
    }

    private string? CpuSensorGate => cpuSensorError.Length > 0 ? cpuSensorError
        : !CpuSensorsEnabled ? "Vypnuto; žádné dotazy na čidla"
        : Paused ? "Sběr je pozastavený; aktuální teplota není dostupná"
        : ResourceError.Length > 0 ? "Čtení prostředků selhalo; aktuální teplota není dostupná"
        : ResourceTime == 0 || NativeMonitor.MonotonicNowNs - ResourceTime > StaleAfterNs
            ? "Čeká na aktuální sběr prostředků"
        : Hardware.TryGetValue("cpu-sensors", out var facts) && facts.GetValueOrDefault("Stav")?.StartsWith("Vypnuto", StringComparison.Ordinal) == true
            ? "Čeká na odpověď po zapnutí CPU čidel" : null;

    internal string CpuSensorStatus => CpuSensorGate
        ?? (Hardware.TryGetValue("cpu-sensors", out var facts) ? facts.GetValueOrDefault("Stav") : null)
        ?? "Čeká na první odpověď čtecího API";

    internal Dictionary<string, string> CpuSensorFacts()
    {
        if (CpuSensorGate is { } gate) return new() { ["Stav CPU čidel"] = gate };
        if (!Hardware.TryGetValue("cpu-sensors", out var facts)) return new() { ["Stav CPU čidel"] = CpuSensorStatus };
        return facts.ToDictionary(pair => "Čidla · " + pair.Key, pair => pair.Value);
    }
}

public sealed partial class MainWindow
{
    private void AddCpuSensorChoice(StackPanel body)
    {
        var choice = new CheckBox { Content = new TextBlock { Text = "Číst CPU čidla z již běžícího místního HTTP API", TextWrapping = TextWrapping.Wrap }, IsChecked = false };
        var help = Label("Volitelně: API kompatibilní s LibreHardwareMonitor na 127.0.0.1:8085, GET /metrics. Poskytovatel a jeho čidla musí být již spuštěné samostatně. Aplikace nic neinstaluje ani nespouští. Identita poskytovatele není ověřena; jiný port nebo staré API nejsou podporované.", 12, 0x95a4ad);
        help.TextWrapping = TextWrapping.Wrap;
        choice.Click += (_, _) => {
            model.SetCpuSensors(choice.IsChecked == true);
            choice.IsChecked = model.CpuSensorsEnabled;
            RefreshHardware();
        };
        body.Children.Add(choice); body.Children.Add(help);
    }
}
