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
        new("processes", "Procesy, CPU a paměť", "Základní výpis a systémová měření.", "Část chráněných podrobností může zůstat nepřístupná.", "Další ovladač se nepoužívá."),
        new("network", "Síťové adaptéry", "Čtení provozu a rychlosti linky.", "Zvýšení práv nenahradí chybějící čítač adaptéru.", "Údaje poskytuje síťový ovladač Windows."),
        new("disk", "Diskové čítače", "Čtení dostupných systémových čítačů.", "Rozsah závisí také na zařízení a jeho zásadách.", "Používá se stávající úložný ovladač."),
        new("gpu", "Vytížení GPU", "Dostupné grafické čítače lze číst bez zvýšení práv.", "Oprávnění sama nezajistí podporu virtuálního adaptéru.", "Čítače musí poskytovat grafický ovladač."),
        new("gpu-temperature", "Teplota GPU", "Čtení podporovaného senzoru grafického adaptéru.", "Zamítnutý přístup a nepodporovaný senzor jsou různé stavy.", "Senzor musí poskytovat grafický ovladač."),
        new("hardware", "CPU, RAM a základní deska", "Čtení údajů, které vystavuje Windows a firmware.", "Chybějící údaj firmwaru může zůstat nedostupný.", "Další ovladač se pro tyto údaje nepoužívá."),
        new("ups", "UPS", "Čtení údajů standardního systémového rozhraní.", "Záleží na připojeném zařízení a jeho rozhraní.", "Používá se stávající ovladač zařízení."),
        new("services", "Stavy a konfigurace služeb", "Čtení služeb dostupných aktuálnímu účtu.", "Záleží na oprávněních nastavených u konkrétní služby.", "Používá se správce služeb Windows, bez dalšího ovladače."),
        new("Threads", "Vlákna procesu", "Dostupnost závisí na právech ke konkrétnímu cíli.", "Některé chráněné cíle zůstanou omezené.", "Dotaz běží v odděleném uživatelském pracovníku."),
        new("Modules", "Moduly procesu", "Čtení seznamu vybraného procesu při dostupných právech.", "Zvýšení práv nemusí zpřístupnit chráněný proces.", "Další ovladač se nepoužívá."),
        new("Memory", "Mapa paměti procesu", "Čtení rozložení při dostupném přístupu k cíli.", "Rozsah se posuzuje pro konkrétní cíl.", "Jde o mapu oblastí, bez úprav jejich obsahu."),
        new("Handles", "Otevřené objekty procesu", "Čtení dostupného výpisu handle vybraného procesu.", "Některé systémové cíle mohou dotaz odmítnout.", "Další ovladač se nepoužívá."),
        new("HandleInfo", "Typ a název objektu", "Vyžaduje právo duplikovat handle cílového procesu.", "Přístup se vyhodnocuje pro konkrétní objekt.", "Blokující dotaz je oddělený do pracovního procesu."),
        new("Signature", "Podpis souboru modulu", "Ověření čitelného vybraného souboru na disku.", "Oprávnění mohou pomoci s přístupem k souboru.", "Používá se místní ověření podpisu a katalogů."),
        new("Owners", "Procesy pro hledání objektů", "Výpis vlastníků s ověřitelnou životností.", "Nedostupní vlastníci se hlásí jako omezení výpisu.", "Další ovladač se nepoužívá."),
        new("Events", "Události procesů ETW", "Spuštění vlastní relace se vyžádá až v okně událostí; Windows může přístup odmítnout.", "Dostupnost určuje oprávnění k systémové relaci. Aplikace práva automaticky nezvyšuje.", "Používá se systémový zdroj událostí; žádný další ovladač se neinstaluje."),
        new("Actions", "Akce nad živým procesem", "Každý cíl se ověřuje na stejném handlu. Běžný pomocník pracuje jen s procesy stejného účtu a relace.", "Podepsaná varianta vyžaduje výslovné zvýšení a potvrzení správce. Odmítá systémovou či cizí relaci, kritické a chráněné cíle; bez důvěryhodného podpisu se nespustí.", "Používají se dostupná uživatelská rozhraní Windows, bez dalšího ovladače."),
        new("Dumps", "Dumpy procesů", "Čtení paměti vybraného procesu x64 nebo WOW64 až po potvrzení. Soubor může obsahovat citlivé údaje.", "Přístup se posuzuje pro konkrétní cíl; tato cesta práva nemění.", "Používá se systémové rozhraní Windows bez dalšího ovladače."),
        new("ServiceActions", "Ovládání služeb", "Právo určuje konkrétní služba. Závislé služby se zastaví jen podle výslovně schváleného plánu.", "Více práv nezaručuje možnost ovládat chráněnou systémovou službu.", "Používá se správce služeb Windows bez dalšího ovladače."),
        new("cpu-temperature", "Teplota CPU", "Volitelné čtení již běžícího místního HTTP API kompatibilního s LibreHardwareMonitor (127.0.0.1:8085). Výchozí stav je vypnutý.", "Zvýšení práv samo nepřidá zdroj měření; aplikace je nevyžaduje.", "Aplikace ovladač ani poskytovatele neinstaluje a nespouští. Čte publikované CPU vzorky s časem; identita poskytovatele není ověřena.")
    ];
    private readonly Dictionary<string, TextBlock> statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> commands = new(StringComparer.Ordinal);
    private readonly TextBlock description = Text("");
    private (long Process, long Resource, long Reports, long Services, bool Paused, bool Stale, bool ServicesStale, string ServicesError, string CpuSensors) stamp;
    private bool initialized;

    internal CapabilityPanel()
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(Text("Aktuální dostupnost měření a výsledky posledních operací. Změna oprávnění není automatická."));
        var list = new StackPanel { Spacing = 4 };
        foreach (var policy in Policies) {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 6, 8, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(Text(policy.Title));
            var status = Text("Zatím bez údaje"); status.Foreground = Display.Brush(0x95a4ad);
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

    private void Explain(Policy policy) => description.Text = policy.Title + "\nBěžný účet: " + policy.Standard
        + "\nZvýšená práva: " + policy.Elevated + "\nOvladač / zdroj: " + policy.Driver;

    internal void Refresh(MonitorModel model, ServiceModel? services)
    {
        var current = (model.ProcessTime, model.ResourceTime, CapabilityReports.Version, services?.Time ?? 0,
            model.Paused, model.Stale, services?.Stale ?? false, services?.Error ?? "", model.CpuSensorStatus);
        if (initialized && stamp == current) return;
        stamp = current; initialized = true;
        statuses["processes"].Text = model.ProcessError.Length > 0 ? "Sběr skončil chybou" : model.ProcessTime == 0 ? "Čeká na vzorek"
            : model.Stale ? "Zastaralá data" : model.Paused ? "Pozastaveno, poslední vzorek" : "Systémový vzorek je dostupný";
        var resources = model.AllResources.ToArray();
        SetMetrics("network", resources.Where(row => row.Kind == ResourceKind.Network).Select(row => row.Stale ? SampleState.Stale : row.Primary.State));
        SetMetrics("disk", resources.Where(row => row.Kind == ResourceKind.Disk).Select(row => row.Stale ? SampleState.Stale : row.Primary.State));
        SetMetrics("gpu", resources.Where(row => row.Kind == ResourceKind.Gpu).Select(row => row.Stale ? SampleState.Stale : row.Primary.State));
        SetMetrics("gpu-temperature", resources.Where(row => row.Kind == ResourceKind.Gpu).Select(row => row.Stale ? SampleState.Stale : row.Gpu?.Temperature.State ?? SampleState.First));
        int facts = model.Hardware.Where(pair => pair.Key is "cpu" or "memory" or "board").Sum(pair => pair.Value.Count);
        statuses["hardware"].Text = facts == 0 ? "Čeká na systémové údaje" : $"Windows poskytl {facts} údajů";
        statuses["ups"].Text = model.Hardware.TryGetValue("ups", out var ups) && ups.Count > 0
            ? string.Join(" · ", ups.Take(2).Select(pair => pair.Key + ": " + pair.Value)) : "Zatím bez hlášení zařízení";
        statuses["cpu-temperature"].Text = model.CpuSensorStatus;
        statuses["services"].Text = services is null || services.Time == 0 ? "Zatím nevyžádáno"
            : services.Error.Length > 0 ? "Čtení služeb skončilo chybou" : services.Stale ? "Zastaralý výpis"
            : services.All.Count + " stavů · " + services.All.Count(row => row.Config.Error == 0) + " konfigurací · "
                + services.All.Count(row => row.Config.Error == 5) + " konfigurací zamítnuto";
        var observed = CapabilityReports.Snapshot();
        foreach (var policy in Policies.Where(policy => policy.Id is "Threads" or "Modules" or "Memory" or "Handles" or "HandleInfo" or "Signature" or "Owners" or "Events" or "Actions" or "Dumps" or "ServiceActions"))
            statuses[policy.Id].Text = observed.TryGetValue(policy.Id, out var value)
                ? value.Status + (value.Target.Pid == 0 ? "" : " · PID " + value.Target.Pid) + " · " + value.ReceivedAt.LocalDateTime.ToString("g")
                : "Zatím nevyžádáno";
        foreach (var policy in Policies) {
            string name = policy.Title + ": " + statuses[policy.Id].Text;
            if (AutomationProperties.GetName(commands[policy.Id]) != name) AutomationProperties.SetName(commands[policy.Id], name);
        }
    }

    private void SetMetrics(string id, IEnumerable<SampleState> states)
    {
        var values = states.GroupBy(value => value).Select(group => group.Count() + "× " + State(group.Key)).ToArray();
        statuses[id].Text = values.Length == 0 ? "Zatím bez adaptéru či vzorku" : string.Join(" · ", values);
    }

    private static string State(SampleState state) => state switch {
        SampleState.Valid => "platný údaj", SampleState.First => "první vzorek", SampleState.AccessDenied => "přístup zamítnut",
        SampleState.Unsupported => "nepodporováno", SampleState.Stale => "zastaralé", SampleState.Error => "chyba",
        SampleState.Gap => "mezera", _ => "nedostupné"
    };

    private static string Privileges()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 8, out var token)) return "Režim oprávnění procesu se nepodařilo zjistit.";
        using (token) {
            if (!GetTokenInformation(token, 20, out uint elevated, sizeof(uint), out _)) return "Režim oprávnění procesu se nepodařilo zjistit.";
            return elevated != 0 ? "Tento proces běží se zvýšenými právy. Dostupnost jednotlivých funkcí vychází z uvedených skutečných výsledků."
                : "Tento proces běží bez zvýšených práv. Ke spuštění nepoužívá další ovladač.";
        }
    }

    [LibraryImport("kernel32.dll")] private static partial nint GetCurrentProcess();
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenProcessToken(nint process, uint access, out SafeFileHandle token);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetTokenInformation(SafeFileHandle token, int type, out uint value, uint length, out uint used);
}
