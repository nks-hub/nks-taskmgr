using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private const string NetworkUnavailable = "nedostupné";
    private static readonly double[] userColumns = [0, 110, 120, 96, 120, 110, 100];
    private readonly Button usersNav = Button("Uživatelé");
    private readonly Grid usersPage = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel userLines = new();
    private readonly TextBlock usersStatus = Label("Načítám relace…", 10, 0x95a4ad);
    private readonly HashSet<uint> expandedSessions = [];
    private readonly Dictionary<uint, SessionLine> sessionLines = [];
    private readonly List<ProcessLine> processLines = [];

    private sealed record SessionLine(Button Button, TextBlock[] Cells);
    private sealed record ProcessLine(Grid Line, TextBlock[] Cells);

    private void BuildUsers()
    {
        usersNav.Click += (_, _) => ShowView(DesktopView.Users);
        usersPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        usersPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        usersPage.RowDefinitions.Add(new RowDefinition());
        usersStatus.Margin = new Thickness(0, 6, 0, 8);
        usersPage.Children.Add(usersStatus);
        var header = UserColumns(out var titles, 0x95a4ad, 11);
        header.Padding = new Thickness(8, 0, 8, 0); header.MinHeight = 30;
        header.BorderBrush = Display.Brush(0x2c363d); header.BorderThickness = new Thickness(0, 0, 0, 1);
        string[] names = ["Uživatel", "Stav", "Relace", "CPU", "Paměť", "Disk", "Síť"];
        string[] tips = [
            "Přihlášený uživatel, počet procesů relace a rozbalení na její procesy.", "Stav relace Windows.", "Číslo relace a způsob připojení.",
            "Součet CPU procesů relace. ≥ označuje známou část, když některý proces nemá aktuální vzorek.",
            "Součet pracovních sad procesů relace. Sdílené stránky RAM mohou být započteny opakovaně; privátní paměť je v popisku řádku.",
            "Součet čtení a zápisu všech I/O procesů (soubory, zařízení, síťové soubory), nejen fyzického disku.",
            "Síťový provoz zatím nelze spolehlivě přiřadit procesům, proto se nesčítá."];
        for (int i = 0; i < titles.Length; ++i) { titles[i].Text = names[i]; ToolTipService.SetToolTip(titles[i], tips[i]); }
        Grid.SetRow(header, 1); usersPage.Children.Add(header);
        var scroll = new ScrollViewer { Content = userLines, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(userLines, "Relace uživatelů");
        Grid.SetRow(scroll, 2); usersPage.Children.Add(scroll);
        page.Children.Add(usersPage);
    }

    private static Grid UserColumns(out TextBlock[] cells, uint color, double size)
    {
        var line = new Grid { ColumnSpacing = 8 };
        cells = new TextBlock[userColumns.Length];
        for (int i = 0; i < userColumns.Length; ++i) {
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(userColumns[i]) });
            var cell = Label("", size, color);
            cell.VerticalAlignment = VerticalAlignment.Center;
            if (i >= 3) cell.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(cell, i); line.Children.Add(cell); cells[i] = cell;
        }
        return line;
    }

    private void ShowUsers(bool visible)
    {
        usersPage.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        usersNav.Background = Display.Brush(visible ? 0x28343bu : 0x12181cu);
        model.SetSessionViewVisible(visible);
        if (!visible) return;
        heading.Text = "Uživatelé"; heading.Visibility = Visibility.Visible;
        search.Visibility = Visibility.Collapsed;
        RefreshUsers();
    }

    private void RefreshUsers()
    {
        var sessions = model.Sessions;
        int processes = sessions.Sum(session => session.Members);
        usersStatus.Text = model.SessionState switch {
            SampleState.First => "Načítám relace…",
            SampleState.Valid => $"{sessions.Count} relací · {processes} procesů",
            SampleState.AccessDenied => "Seznam relací Windows vyžaduje oprávnění; relace jsou odvozené jen z procesů.",
            _ => "Seznam relací Windows je nedostupný; relace jsou odvozené jen z procesů."
        } + (model.Stale ? " · údaje jsou zastaralé" : "") + " · odpojení a odhlášení zatím nejsou k dispozici";
        var members = new Dictionary<uint, List<ProcessRow>>();
        foreach (var row in model.Processes) {
            if (row.Key.Pid == 0 || !model.ProcessSessions.TryGetValue(row.Key, out uint id) || !expandedSessions.Contains(id)) continue;
            if (!members.TryGetValue(id, out var list)) members.Add(id, list = []);
            list.Add(row);
        }
        var wanted = new List<UIElement>();
        int used = 0;
        foreach (var session in sessions) {
            var line = SessionRow(session);
            wanted.Add(line.Button);
            if (!expandedSessions.Contains(session.Id) || !members.TryGetValue(session.Id, out var rows)) continue;
            rows.Sort((a, b) => {
                int order = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
                return order != 0 ? order : a.Key.Pid.CompareTo(b.Key.Pid);
            });
            foreach (var row in rows) wanted.Add(ProcessRowLine(used++, row));
        }
        foreach (var id in sessionLines.Keys.Where(id => !sessions.Any(session => session.Id == id)).ToArray()) sessionLines.Remove(id);
        expandedSessions.RemoveWhere(id => !sessionLines.ContainsKey(id));
        if (!userLines.Children.SequenceEqual(wanted)) {
            userLines.Children.Clear();
            foreach (var element in wanted) userLines.Children.Add(element);
        }
    }

    private SessionLine SessionRow(UserSession session)
    {
        if (!sessionLines.TryGetValue(session.Id, out var line)) {
            var grid = UserColumns(out var cells, 0xe9eef0, 12);
            var button = new Button {
                Content = grid, MinHeight = 0, Height = 34, Padding = new Thickness(8, 0, 8, 0), FontFamily = UiTypography.Font,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Display.Brush(0x171d21), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0)
            };
            uint id = session.Id;
            button.Click += (_, _) => { if (!expandedSessions.Add(id)) expandedSessions.Remove(id); RefreshUsers(); };
            sessionLines.Add(id, line = new SessionLine(button, cells));
        }
        bool expanded = expandedSessions.Contains(session.Id);
        string name = session.User.Length > 0 ? session.User
            : session.InfoState == SampleState.AccessDenied ? "Nedostupné bez správce"
            : session.Id == 0 ? "Služby systému" : session.Station.Length > 0 ? session.Station : "Neznámý uživatel";
        string protocol = session.Protocol switch {
            SessionProtocol.Console => "konzole", SessionProtocol.Rdp => "RDP", SessionProtocol.Ica => "ICA",
            _ => session.InfoState == SampleState.AccessDenied ? "nedostupné" : "neznámé"
        };
        string state = session.State switch {
            SessionConnectState.Active => "Aktivní", SessionConnectState.Connected => "Připojeno", SessionConnectState.ConnectQuery => "Připojování",
            SessionConnectState.Shadow => "Stínování", SessionConnectState.Disconnected => "Odpojeno", SessionConnectState.Idle => "Nečinná",
            SessionConnectState.Listen => "Naslouchá", SessionConnectState.Reset => "Resetována", SessionConnectState.Down => "Mimo provoz",
            SessionConnectState.Init => "Inicializace", _ => "Neznámý"
        };
        string[] texts = [
            (expanded ? "⌄ " : "› ") + name + $" ({session.Members})", state, $"{session.Id} · {protocol}",
            session.Cpu.Text(session.Members, false, " %"),
            session.Working.Text(session.Members, false), session.Io.Text(session.Members, false, " MiB/s", 1048576), NetworkUnavailable];
        for (int i = 0; i < texts.Length; ++i) if (line.Cells[i].Text != texts[i]) line.Cells[i].Text = texts[i];
        line.Cells[6].Foreground = Display.Brush(0x95a4ad);
        string account = session.User.Length == 0 ? name : session.Domain.Length > 0 ? session.Domain + "\\" + session.User : session.User;
        string tip = $"{account} · relace {session.Id}" + (session.Station.Length > 0 ? $" · stanice {session.Station}" : "") + "\n"
            + (session.InfoState == SampleState.AccessDenied ? "Údaje o uživateli této relace vyžadují oprávnění správce.\n" : "")
            + $"Známé aktuální vzorky: CPU {session.Cpu.Known}/{session.Members}, paměť {session.Working.Known}/{session.Members}, I/O {session.Io.Known}/{session.Members}.\n"
            + $"Privátní paměť: {session.Private.Text(session.Members, false)}.\n"
            + "≥ označuje známou část neúplného součtu. Sdílené stránky RAM mohou být započteny opakovaně.";
        if (ToolTipService.GetToolTip(line.Button) as string != tip) ToolTipService.SetToolTip(line.Button, tip);
        AutomationProperties.SetName(line.Button, $"{name}, {state}, relace {session.Id}, {session.Members} procesů, {(expanded ? "rozbaleno" : "sbaleno")}");
        return line;
    }

    private UIElement ProcessRowLine(int index, ProcessRow row)
    {
        if (index == processLines.Count) {
            var grid = UserColumns(out var cells, 0xd7dfe3, 11);
            grid.Padding = new Thickness(34, 0, 8, 0); grid.MinHeight = 26;
            processLines.Add(new ProcessLine(grid, cells));
        }
        var line = processLines[index];
        string disk = row.Ended || row.Stale ? row.ReadText
            : row.ReadIo.State == SampleState.Valid && row.WriteIo.State == SampleState.Valid
                ? Display.Number((row.ReadIo.Value + row.WriteIo.Value) / 1048576, " MiB/s")
                : Display.Metric(row.ReadIo.State != SampleState.Valid ? row.ReadIo : row.WriteIo, " MiB/s", 1048576);
        string[] texts = [row.Name, "", "PID " + row.PidText, row.CpuText, row.WorkingText, disk, ""];
        for (int i = 0; i < texts.Length; ++i) if (line.Cells[i].Text != texts[i]) line.Cells[i].Text = texts[i];
        return line.Line;
    }
}
