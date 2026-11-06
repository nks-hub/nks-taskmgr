using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;
using System.Runtime.InteropServices;

namespace NksTaskMgr.WinUI;

internal sealed partial class CapabilityPanel : UserControl
{
    private sealed record Policy(string Id, string Title, string Standard, string Elevated, string Driver);
    private static readonly Policy[] Policies = [
        new("processes", Loc.T("Procesy, CPU a paměť"), Loc.T("Základní výpis a systémová měření."), Loc.T("Část chráněných podrobností může zůstat nepřístupná."), Loc.T("Další ovladač se nepoužívá.")),
        new("network", Loc.T("Síťové adaptéry"), Loc.T("Čtení provozu a rychlosti linky."), Loc.T("Zvýšení práv nenahradí chybějící čítač adaptéru."), Loc.T("Údaje poskytuje síťový ovladač Windows.")),
        new("disk", Loc.T("Diskové čítače"), Loc.T("Čtení dostupných systémových čítačů."), Loc.T("Rozsah závisí také na zařízení a jeho zásadách."), Loc.T("Používá se stávající úložný ovladač.")),
        new("gpu", Loc.T("Vytížení GPU"), Loc.T("Dostupné grafické čítače lze číst bez zvýšení práv."), Loc.T("Oprávnění sama nezajistí podporu virtuálního adaptéru."), Loc.T("Čítače musí poskytovat grafický ovladač.")),
        new("gpu-temperature", Loc.T("Teplota GPU"), Loc.T("Čtení podporovaného senzoru grafického adaptéru."), Loc.T("Zamítnutý přístup a nepodporovaný senzor jsou různé stavy."), Loc.T("Senzor musí poskytovat grafický ovladač.")),
        new("hardware", Loc.T("CPU, RAM a základní deska"), Loc.T("Čtení údajů, které vystavuje Windows a firmware."), Loc.T("Chybějící údaj firmwaru může zůstat nedostupný."), Loc.T("Další ovladač se pro tyto údaje nepoužívá.")),
        new("ups", Loc.T("UPS"), Loc.T("Čtení údajů standardního systémového rozhraní."), Loc.T("Záleží na připojeném zařízení a jeho rozhraní."), Loc.T("Používá se stávající ovladač zařízení.")),
        new("services", Loc.T("Stavy a konfigurace služeb"), Loc.T("Čtení služeb dostupných aktuálnímu účtu."), Loc.T("Záleží na oprávněních nastavených u konkrétní služby."), Loc.T("Používá se správce služeb Windows, bez dalšího ovladače.")),
        new("Threads", Loc.T("Vlákna procesu"), Loc.T("Dostupnost závisí na právech ke konkrétnímu cíli."), Loc.T("Některé chráněné cíle zůstanou omezené."), Loc.T("Dotaz běží v odděleném uživatelském pracovníku.")),
        new("Modules", Loc.T("Moduly procesu"), Loc.T("Čtení seznamu vybraného procesu při dostupných právech."), Loc.T("Zvýšení práv nemusí zpřístupnit chráněný proces."), Loc.T("Další ovladač se nepoužívá.")),
        new("Memory", Loc.T("Mapa paměti procesu"), Loc.T("Čtení rozložení při dostupném přístupu k cíli."), Loc.T("Rozsah se posuzuje pro konkrétní cíl."), Loc.T("Jde o mapu oblastí, bez úprav jejich obsahu.")),
        new("Handles", Loc.T("Otevřené objekty procesu"), Loc.T("Čtení dostupného výpisu handle vybraného procesu."), Loc.T("Některé systémové cíle mohou dotaz odmítnout."), Loc.T("Další ovladač se nepoužívá.")),
        new("HandleInfo", Loc.T("Typ a název objektu"), Loc.T("Vyžaduje právo duplikovat handle cílového procesu."), Loc.T("Přístup se vyhodnocuje pro konkrétní objekt."), Loc.T("Blokující dotaz je oddělený do pracovního procesu.")),
        new("Signature", Loc.T("Podpis souboru modulu"), Loc.T("Ověření čitelného vybraného souboru na disku."), Loc.T("Oprávnění mohou pomoci s přístupem k souboru."), Loc.T("Používá se místní ověření podpisu a katalogů.")),
        new("Owners", Loc.T("Procesy pro hledání objektů"), Loc.T("Výpis vlastníků s ověřitelnou životností."), Loc.T("Nedostupní vlastníci se hlásí jako omezení výpisu."), Loc.T("Další ovladač se nepoužívá.")),
        new("Events", Loc.T("Události procesů ETW"), Loc.T("Spuštění vlastní relace se vyžádá až v okně událostí; Windows může přístup odmítnout."), Loc.T("Dostupnost určuje oprávnění k systémové relaci. Aplikace práva automaticky nezvyšuje."), Loc.T("Používá se systémový zdroj událostí; žádný další ovladač se neinstaluje.")),
        new("Actions", Loc.T("Akce nad živým procesem"), Loc.T("Každý cíl se ověřuje na stejném handlu. Běžný pomocník pracuje jen s procesy stejného účtu a relace."), Loc.T("Podepsaná varianta vyžaduje výslovné zvýšení a potvrzení správce. Odmítá systémovou či cizí relaci, kritické a chráněné cíle; bez důvěryhodného podpisu se nespustí."), Loc.T("Používají se dostupná uživatelská rozhraní Windows, bez dalšího ovladače.")),
        new("Dumps", Loc.T("Dumpy procesů"), Loc.T("Čtení paměti vybraného procesu x64 nebo WOW64 až po potvrzení. Soubor může obsahovat citlivé údaje."), Loc.T("Přístup se posuzuje pro konkrétní cíl; tato cesta práva nemění."), Loc.T("Používá se systémové rozhraní Windows bez dalšího ovladače.")),
        new("ServiceActions", Loc.T("Ovládání služeb"), Loc.T("Právo určuje konkrétní služba. Závislé služby se zastaví jen podle výslovně schváleného plánu."), Loc.T("Více práv nezaručuje možnost ovládat chráněnou systémovou službu."), Loc.T("Používá se správce služeb Windows bez dalšího ovladače.")),
        new("cpu-temperature", Loc.T("Teplota CPU"), Loc.T("Volitelné čtení již běžícího místního HTTP API kompatibilního s LibreHardwareMonitor (127.0.0.1:8085). Výchozí stav je vypnutý."), Loc.T("Zvýšení práv samo nepřidá zdroj měření; aplikace je nevyžaduje."), Loc.T("Aplikace ovladač ani poskytovatele neinstaluje a nespouští. Čte publikované CPU vzorky s časem; identita poskytovatele není ověřena."))
    ];
    private readonly Dictionary<string, TextBlock> statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> commands = new(StringComparer.Ordinal);
    private readonly TextBlock description = Text("");
    private (long Process, long Resource, long Reports, long Services, bool Paused, bool Stale, bool ServicesStale, string ServicesError, string CpuSensors) stamp;
    private bool initialized;

    internal CapabilityPanel()
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(Text(Loc.T("Aktuální dostupnost měření a výsledky posledních operací. Změna oprávnění není automatická.")));
        var list = new StackPanel { Spacing = 4 };
        foreach (var policy in Policies) {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 6, 8, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(Text(policy.Title));
            var status = Text(Loc.T("Zatím bez údaje")); status.Foreground = Display.Brush(0x95a4ad);
            statuses.Add(policy.Id, status); Grid.SetColumn(status, 1); row.Children.Add(status);
            var button = new Button { Content = row, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 34, Background = Display.Brush(0x1d252a) };
            AutomationProperties.SetName(button, policy.Title);
            commands.Add(policy.Id, button);
            button.Click += (_, _) => Explain(policy); list.Children.Add(button);
        }
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 340, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        description.TextWrapping = TextWrapping.Wrap; body.Children.Add(description);
        body.Children.Add(Text(Privileges()));
        Content = body; Explain(Policies[0]);
    }

    private static TextBlock Text(string text) => new() { Text = text, FontFamily = UiTypography.Font, FontSize = 12,
        Foreground = Display.Brush(0xe9eef0), TextWrapping = TextWrapping.Wrap };

    private void Explain(Policy policy) => description.Text = Loc.F("{0}\nBěžný účet: {1}\nZvýšená práva: {2}\nOvladač / zdroj: {3}",
        policy.Title, policy.Standard, policy.Elevated, policy.Driver);

    internal void Refresh(MonitorModel model, ServiceModel? services)
    {
        var current = (model.ProcessTime, model.ResourceTime, CapabilityReports.Version, services?.Time ?? 0,
            model.Paused, model.Stale, services?.Stale ?? false, services?.Error ?? "", model.CpuSensorStatus);
        if (initialized && stamp == current) return;
        stamp = current; initialized = true;
        statuses["processes"].Text = model.ProcessError.Length > 0 ? Loc.T("Sběr skončil chybou") : model.ProcessTime == 0 ? Loc.T("Čeká na vzorek")
            : model.Stale ? Loc.T("Zastaralá data") : model.Paused ? Loc.T("Pozastaveno, poslední vzorek") : Loc.T("Systémový vzorek je dostupný");
        var resources = model.AllResources.ToArray();
        SetMetrics("network", resources.Where(row => row.Kind == ResourceKind.Network).Select(row => row.Stale ? SampleState.Stale : row.Primary.State));
        SetMetrics("disk", resources.Where(row => row.Kind == ResourceKind.Disk).Select(row => row.Stale ? SampleState.Stale : row.Primary.State));
        SetMetrics("gpu", resources.Where(row => row.Kind == ResourceKind.Gpu).Select(row => row.Stale ? SampleState.Stale : row.Primary.State));
        SetMetrics("gpu-temperature", resources.Where(row => row.Kind == ResourceKind.Gpu).Select(row => row.Stale ? SampleState.Stale : row.Gpu?.Temperature.State ?? SampleState.First));
        int facts = model.Hardware.Where(pair => pair.Key is "cpu" or "memory" or "board").Sum(pair => pair.Value.Count);
        statuses["hardware"].Text = facts == 0 ? Loc.T("Čeká na systémové údaje") : Loc.F("Windows poskytl {0} údajů", facts);
        statuses["ups"].Text = model.Hardware.TryGetValue("ups", out var ups) && ups.Count > 0
            ? string.Join(" · ", ups.Take(2).Select(pair => pair.Key + ": " + pair.Value)) : Loc.T("Zatím bez hlášení zařízení");
        statuses["cpu-temperature"].Text = model.CpuSensorStatus;
        statuses["services"].Text = services is null || services.Time == 0 ? Loc.T("Zatím nevyžádáno")
            : services.Error.Length > 0 ? Loc.T("Čtení služeb skončilo chybou") : services.Stale ? Loc.T("Zastaralý výpis")
            : Loc.F("{0} stavů · {1} konfigurací · {2} konfigurací zamítnuto", services.All.Count,
                services.All.Count(row => row.Config.Error == 0), services.All.Count(row => row.Config.Error == 5));
        var observed = CapabilityReports.Snapshot();
        foreach (var policy in Policies.Where(policy => policy.Id is "Threads" or "Modules" or "Memory" or "Handles" or "HandleInfo" or "Signature" or "Owners" or "Events" or "Actions" or "Dumps" or "ServiceActions"))
            statuses[policy.Id].Text = observed.TryGetValue(policy.Id, out var value)
                ? value.Status + (value.Target.Pid == 0 ? "" : " · PID " + value.Target.Pid) + " · " + value.ReceivedAt.LocalDateTime.ToString("g")
                : Loc.T("Zatím nevyžádáno");
        foreach (var policy in Policies) {
            string name = policy.Title + ": " + statuses[policy.Id].Text;
            if (AutomationProperties.GetName(commands[policy.Id]) != name) AutomationProperties.SetName(commands[policy.Id], name);
        }
    }

    private void SetMetrics(string id, IEnumerable<SampleState> states)
    {
        var values = states.GroupBy(value => value).Select(group => group.Count() + "× " + State(group.Key)).ToArray();
        statuses[id].Text = values.Length == 0 ? Loc.T("Zatím bez adaptéru či vzorku") : string.Join(" · ", values);
    }

    private static string State(SampleState state) => state switch {
        SampleState.Valid => Loc.T("platný údaj"), SampleState.First => Loc.T("první vzorek"), SampleState.AccessDenied => Loc.T("přístup zamítnut"),
        SampleState.Unsupported => Loc.T("nepodporováno"), SampleState.Stale => Loc.T("zastaralé"), SampleState.Error => Loc.T("chyba"),
        SampleState.Gap => Loc.T("mezera"), _ => Loc.T("nedostupné")
    };

    private static string Privileges()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 8, out var token)) return Loc.T("Režim oprávnění procesu se nepodařilo zjistit.");
        using (token) {
            if (!GetTokenInformation(token, 20, out uint elevated, sizeof(uint), out _)) return Loc.T("Režim oprávnění procesu se nepodařilo zjistit.");
            return elevated != 0 ? Loc.T("Tento proces běží se zvýšenými právy. Dostupnost jednotlivých funkcí vychází z uvedených skutečných výsledků.")
                : Loc.T("Tento proces běží bez zvýšených práv. Ke spuštění nepoužívá další ovladač.");
        }
    }

    [LibraryImport("kernel32.dll")] private static partial nint GetCurrentProcess();
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenProcessToken(nint process, uint access, out SafeFileHandle token);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetTokenInformation(SafeFileHandle token, int type, out uint value, uint length, out uint used);
}
