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
            cpuSensorError = Loc.T("Tato knihovna čtení CPU čidel nepodporuje");
        }
        catch (NativeMonitorException) {
            CpuSensorsEnabled = false;
            cpuSensorError = Loc.T("Změnu čtení CPU čidel knihovna nepotvrdila");
        }
    }

    private string? CpuSensorGate => cpuSensorError.Length > 0 ? cpuSensorError
        : !CpuSensorsEnabled ? Loc.T("Vypnuto; žádné dotazy na čidla")
        : Paused ? Loc.T("Sběr je pozastavený; aktuální teplota není dostupná")
        : ResourceError.Length > 0 ? Loc.T("Čtení prostředků selhalo; aktuální teplota není dostupná")
        : ResourceTime == 0 || NativeMonitor.MonotonicNowNs - ResourceTime > StaleAfterNs
            ? Loc.T("Čeká na aktuální sběr prostředků")
        : Hardware.TryGetValue("cpu-sensors", out var facts) && facts.GetValueOrDefault("Stav")?.StartsWith("Vypnuto", StringComparison.Ordinal) == true
            ? Loc.T("Čeká na odpověď po zapnutí CPU čidel") : null;

    internal string CpuSensorStatus => CpuSensorGate
        ?? (Hardware.TryGetValue("cpu-sensors", out var facts) ? facts.GetValueOrDefault("Stav") switch {
            "Poskytovatel nedostupný" => Loc.T("Externí program s čidly neběží nebo není dostupný"),
            "Chyba čtecího API" => Loc.T("Externí program neposkytl použitelné údaje z čidel"),
            "Nepodporováno" => Loc.T("Externí program neposkytuje podporované teplotní čidlo CPU"),
            var status => status
        } : null)
        ?? Loc.T("Čeká na první odpověď externího programu s čidly");

    internal IEnumerable<KeyValuePair<string, string>> CpuSensorSummaryFacts()
    {
        yield return new(Loc.T("Stav CPU čidel"), CpuSensorStatus);
        if (CpuSensorGate is not null || !Hardware.TryGetValue("cpu-sensors", out var facts)) yield break;
        foreach (var (name, value) in facts) {
            if (name is "Stav" or "Zdroj" or "Adresa čtení" or "Poslední čtení API" or "Význam měření" or "Platnost čtení" or "Aktualizace" or "Omezení") continue;
            int separator = value.IndexOf(" · ", StringComparison.Ordinal);
            string reading = value;
            if (separator > 0 && value[..separator].EndsWith(" °C", StringComparison.Ordinal)) {
                reading = value[..separator];
                if (!value.Contains(" · publikovaný vzorek", StringComparison.Ordinal)) reading += " · " + Loc.T("zastaralý nebo nejistý vzorek");
            }
            int identifier = name.LastIndexOf(" [", StringComparison.Ordinal);
            string title = identifier > 0 && name.EndsWith(']') ? name[..identifier] : name;
            yield return new(Loc.F("Teplota · {0}", title), reading);
        }
    }

    internal Dictionary<string, string> CpuSensorFacts()
    {
        if (CpuSensorGate is { } gate) return new() { [Loc.T("Stav CPU čidel")] = gate };
        if (!Hardware.TryGetValue("cpu-sensors", out var facts)) return new() { [Loc.T("Stav CPU čidel")] = CpuSensorStatus };
        return facts.ToDictionary(pair => Loc.F("Čidla · {0}", pair.Key), pair => pair.Value);
    }
}

public sealed partial class MainWindow
{
    private CheckBox cpuSensorChoice = null!;
    private Expander cpuSensorDetails = null!;
    private readonly HardwareFactRows cpuSensorDetailsRows = new();

    private void AddCpuSensorChoice(Grid heading)
    {
        cpuSensorChoice = new CheckBox { Content = Loc.T("Teplota z čidel"), IsChecked = false, MinWidth = 0, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(cpuSensorChoice, Loc.T("Číst teplotu CPU z již spuštěného programu kompatibilního s LibreHardwareMonitor (127.0.0.1:8085).")
            + " " + Loc.T("Tato aplikace jej neinstaluje ani nespouští a identitu externího zdroje neověřuje."));
        cpuSensorChoice.Click += (_, _) => {
            model.SetCpuSensors(cpuSensorChoice.IsChecked == true);
            cpuSensorChoice.IsChecked = model.CpuSensorsEnabled;
            RefreshHardware();
        };
        Grid.SetColumn(cpuSensorChoice, 1); heading.Children.Add(cpuSensorChoice);
    }

    private void AddCpuSensorDetails(StackPanel body)
    {
        var content = new StackPanel { Spacing = 12 };
        var help = Label(Loc.T("Čte se místní HTTP API kompatibilní s LibreHardwareMonitor na 127.0.0.1:8085, GET /metrics. Poskytovatel a jeho čidla musí být již spuštěné samostatně. Aplikace nic neinstaluje ani nespouští. Identita poskytovatele není ověřena; jiný port nebo staré API nejsou podporované."), 12, 0x95a4ad);
        help.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(help); content.Children.Add(cpuSensorDetailsRows.Panel);
        cpuSensorDetails = new Expander { Header = Loc.T("Podrobnosti čidel"), Content = content, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        body.Children.Add(cpuSensorDetails);
    }
}
