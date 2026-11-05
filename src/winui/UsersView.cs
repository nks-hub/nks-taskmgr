using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NksTaskMgr.Native;
using Windows.System;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private static readonly double[] userColumns = [0, 104, 44, 104, 120, 80, 110, 100, 90];
    private const int FirstMetricColumn = 5;
    private readonly Button usersNav = Button("Uživatelé");
    private readonly Grid usersPage = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel userLines = new();
    private readonly TextBlock usersStatus = Label("Načítám relace…", 10, 0x95a4ad);
    private readonly Button disconnectUser = Button("Odpojit"), logoffUser = Button("Odhlásit se"), messageUser = Button("Odeslat zprávu…"),
        manageAccounts = Button("Spravovat uživatelské účty"), usersOptions = Button("Zobrazit");
    private readonly ToggleMenuFlyoutItem fullAccountName = new() { Text = "Zobrazit celý název účtu" };
    private readonly HashSet<uint> expandedSessions = [];
    private readonly Dictionary<uint, SessionLine> sessionLines = [];
    private readonly List<ProcessLine> processLines = [];
    private readonly Dictionary<uint, (ProcessBranchMetric Sum, int Count)> sessionNetwork = [];
    private readonly Dictionary<string, string> fullNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> fullNamesPending = new(StringComparer.OrdinalIgnoreCase);
    private uint? selectedSession;
    private bool userActionBusy;

    private sealed record SessionLine(Button Button, TextBlock[] Cells);
    private sealed record ProcessLine(Grid Line, TextBlock[] Cells);

    private void BuildUsers()
    {
        usersNav.Click += (_, _) => ShowView(DesktopView.Users);
        usersPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        usersPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        usersPage.RowDefinitions.Add(new RowDefinition());
        var toolbar = new Grid { MinHeight = 34, Margin = new Thickness(0, 0, 0, 6) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        usersStatus.VerticalAlignment = VerticalAlignment.Center; toolbar.Children.Add(usersStatus);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        disconnectUser.Click += async (_, _) => await RunUserActionAsync(UserSessionAction.Disconnect);
        logoffUser.Click += async (_, _) => await RunUserActionAsync(UserSessionAction.Logoff);
        messageUser.Click += async (_, _) => await RunUserActionAsync(UserSessionAction.Message);
        manageAccounts.Click += (_, _) => OpenUserAccounts();
        ToolTipService.SetToolTip(disconnectUser, "Odpojit vybraného uživatele. Jeho relace a programy dál běží.");
        ToolTipService.SetToolTip(logoffUser, "Odhlásit vybraného uživatele. Neuložená data jeho programů se mohou ztratit.");
        ToolTipService.SetToolTip(messageUser, "Zobrazit zprávu na ploše vybraného uživatele.");
        ToolTipService.SetToolTip(manageAccounts, "Otevřít stránku Nastavení pro správu uživatelských účtů");
        var options = new MenuFlyout();
        options.Items.Add(ContextItem("Rozbalit vše", "users-expand-all", true, () => { foreach (var session in model.Sessions) expandedSessions.Add(session.Id); RefreshUsers(); }));
        options.Items.Add(ContextItem("Sbalit vše", "users-collapse-all", true, () => { expandedSessions.Clear(); RefreshUsers(); }));
        options.Items.Add(new MenuFlyoutSeparator());
        fullAccountName.Click += (_, _) => RefreshUsers();
        options.Items.Add(fullAccountName);
        usersOptions.Flyout = options;
        foreach (var button in new[] { disconnectUser, logoffUser, messageUser, manageAccounts, usersOptions }) { button.Height = 28; actions.Children.Add(button); }
        Grid.SetColumn(actions, 1); toolbar.Children.Add(actions);
        usersPage.Children.Add(toolbar);
        var header = UserColumns(out var titles, 0x95a4ad, 11);
        header.Padding = new Thickness(8, 0, 8, 0); header.MinHeight = 30;
        header.BorderBrush = Display.Brush(0x2c363d); header.BorderThickness = new Thickness(0, 0, 0, 1);
        string[] names = ["Uživatel", "Stav", "ID", "Relace", "Název klienta", "CPU", "Paměť", "Disk", "Síť"];
        string[] tips = [
            "Přihlášený uživatel a počet procesů relace. Dvojklik nebo šipka vpravo rozbalí procesy relace.", "Stav relace Windows.", "Číslo relace.",
            "Název stanice relace, například Console nebo RDP-Tcp#2.", "Název počítače, ze kterého je relace připojena vzdáleně.",
            "Součet CPU procesů relace. ≥ označuje známou část, když některý proces nemá aktuální vzorek.",
            "Součet pracovních sad procesů relace. Sdílené stránky RAM mohou být započteny opakovaně; privátní paměť je v popisku řádku.",
            "Součet čtení a zápisu všech I/O procesů (soubory, zařízení, síťové soubory), nejen fyzického disku.",
            "Součet odeslaných a přijatých bajtů TCP/UDP procesů relace. Měří se jen během vlastního záznamu ETW v okně Události procesů,"
                + " který vyžaduje správce; jinak nedostupné. ≥ označuje známou část, když provoz nebo některý proces nešlo přiřadit."];
        for (int i = 0; i < titles.Length; ++i) { titles[i].Text = names[i]; ToolTipService.SetToolTip(titles[i], tips[i]); }
        Grid.SetRow(header, 1); usersPage.Children.Add(header);
        var scroll = new ScrollViewer { Content = userLines, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(userLines, "Relace uživatelů");
        Grid.SetRow(scroll, 2); usersPage.Children.Add(scroll);
        page.Children.Add(usersPage);
        model.SessionNotifications = UserSessionApi.Watch(windowHandle);
        Closed += (_, _) => { if (model.SessionNotifications) UserSessionApi.Unwatch(windowHandle); };
        RefreshUserActions();
    }

    private static Grid UserColumns(out TextBlock[] cells, uint color, double size)
    {
        var line = new Grid { ColumnSpacing = 8 };
        cells = new TextBlock[userColumns.Length];
        for (int i = 0; i < userColumns.Length; ++i) {
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(userColumns[i]) });
            var cell = Label("", size, color);
            cell.VerticalAlignment = VerticalAlignment.Center;
            if (i >= FirstMetricColumn || i == 2) cell.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(cell, i); line.Children.Add(cell); cells[i] = cell;
        }
        return line;
    }

    private void ShowUsers(bool visible)
    {
        usersPage.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MarkNavigation(usersNav, visible);
        model.SetSessionViewVisible(visible);
        if (!visible) return;
        heading.Text = "Uživatelé"; heading.Visibility = Visibility.Visible;
        search.Visibility = Visibility.Collapsed;
        RefreshUsers();
    }

    private UserSession? SelectedUserSession() => selectedSession is { } id ? model.Sessions.FirstOrDefault(session => session.Id == id) : null;

    private void RefreshUserActions()
    {
        var session = SelectedUserSession();
        disconnectUser.IsEnabled = !userActionBusy && session is not null && UserSessionText.CanRun(UserSessionAction.Disconnect, session.State);
        logoffUser.IsEnabled = !userActionBusy && session is not null && UserSessionText.CanRun(UserSessionAction.Logoff, session.State);
        messageUser.IsEnabled = !userActionBusy && session is not null && UserSessionText.CanRun(UserSessionAction.Message, session.State);
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
        } + (model.Stale ? " · údaje jsou zastaralé" : "");
        var members = new Dictionary<uint, List<ProcessRow>>();
        sessionNetwork.Clear();
        foreach (var row in model.Processes) {
            if (row.Key.Pid == 0 || !model.ProcessSessions.TryGetValue(row.Key, out uint id)) continue;
            var (sum, count) = sessionNetwork.GetValueOrDefault(id);
            sessionNetwork[id] = (sum.Add(row.Ended || row.Stale ? default : row.Network), count + 1);
            if (!expandedSessions.Contains(id)) continue;
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
        if (selectedSession is { } selected && !sessionLines.ContainsKey(selected)) selectedSession = null;
        if (!userLines.Children.SequenceEqual(wanted)) {
            userLines.Children.Clear();
            foreach (var element in wanted) userLines.Children.Add(element);
        }
        RefreshUserActions();
    }

    private void SelectSession(uint id)
    {
        selectedSession = id;
        RefreshUsers();
    }

    private void SetSessionExpanded(uint id, bool expanded)
    {
        if (expanded) expandedSessions.Add(id); else expandedSessions.Remove(id);
        RefreshUsers();
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
            button.Click += (_, _) => SelectSession(id);
            button.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler((_, _) => SetSessionExpanded(id, !expandedSessions.Contains(id))), true);
            button.KeyDown += (_, args) => {
                if (args.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
                selectedSession = id; SetSessionExpanded(id, args.Key == VirtualKey.Right); args.Handled = true;
            };
            button.ContextRequested += (_, args) => UserContextRequested(id, button, args);
            sessionLines.Add(id, line = new SessionLine(button, cells));
        }
        bool expanded = expandedSessions.Contains(session.Id), selected = selectedSession == session.Id;
        bool showFull = fullAccountName.IsChecked;
        string name = session.User.Length > 0 ? UserSessionText.Name(session.User, session.Domain, showFull ? FullName(session) : "", showFull)
            : session.InfoState == SampleState.AccessDenied ? "Nedostupné bez správce"
            : session.Id == 0 ? "Služby systému" : session.Station.Length > 0 ? session.Station : "Neznámý uživatel";
        string protocol = session.Protocol switch {
            SessionProtocol.Console => "konzole", SessionProtocol.Rdp => "RDP", SessionProtocol.Ica => "ICA",
            _ => session.InfoState == SampleState.AccessDenied ? "nedostupné" : "neznámé"
        };
        string state = UserSessionText.State(session.State);
        var (network, networkMembers) = sessionNetwork.GetValueOrDefault(session.Id);
        string[] texts = [
            (expanded ? "⌄ " : "› ") + UserSessionText.Title(name, session.Members), state, session.Id.ToString(), session.Station, session.Client,
            session.Cpu.Text(session.Members, false, " %"),
            session.Working.Text(session.Members, false), session.Io.Text(session.Members, false, " MiB/s", 1048576),
            network.NetworkText(networkMembers, networkMembers != session.Members)];
        for (int i = 0; i < texts.Length; ++i) if (line.Cells[i].Text != texts[i]) line.Cells[i].Text = texts[i];
        line.Cells[^1].Foreground = Display.Brush(network.Known == 0 ? 0x95a4adu : 0xe9eef0u);
        line.Button.Background = Display.Brush(selected ? 0x28343bu : 0x171d21u);
        string account = session.User.Length == 0 ? name : UserSessionText.Account(session.User, session.Domain);
        string tip = $"{account} · relace {session.Id} · {protocol}" + (session.Station.Length > 0 ? $" · stanice {session.Station}" : "")
            + (session.Client.Length > 0 ? $" · klient {session.Client}" : "") + "\n"
            + (session.InfoState == SampleState.AccessDenied ? "Údaje o uživateli této relace vyžadují oprávnění správce.\n" : "")
            + $"Známé aktuální vzorky: CPU {session.Cpu.Known}/{session.Members}, paměť {session.Working.Known}/{session.Members}, I/O {session.Io.Known}/{session.Members}, "
            + (model.NetworkAvailable ? $"síť {network.Known}/{session.Members}.\n" : "síť jen během záznamu ETW se správcem.\n")
            + $"Privátní paměť: {session.Private.Text(session.Members, false)}.\n"
            + "≥ označuje známou část neúplného součtu. Sdílené stránky RAM mohou být započteny opakovaně.";
        if (ToolTipService.GetToolTip(line.Button) as string != tip) ToolTipService.SetToolTip(line.Button, tip);
        AutomationProperties.SetName(line.Button, $"{name}, {state}, relace {session.Id}, {session.Members} procesů, {(expanded ? "rozbaleno" : "sbaleno")}"
            + (selected ? ", vybráno" : ""));
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
        string[] texts = [row.Name, "", "", "PID " + row.PidText, "", row.CpuText, row.WorkingText, disk, row.NetworkText];
        for (int i = 0; i < texts.Length; ++i) if (line.Cells[i].Text != texts[i]) line.Cells[i].Text = texts[i];
        return line.Line;
    }

    private void UserContextRequested(uint id, Button owner, ContextRequestedEventArgs args)
    {
        if (args.Handled || view != DesktopView.Users) return;
        SelectSession(id);
        bool expanded = expandedSessions.Contains(id);
        var menu = new MenuFlyout();
        menu.Items.Add(ContextItem("Rozbalit", "context-user-expand", !expanded, () => SetSessionExpanded(id, true)));
        menu.Items.Add(ContextItem("Sbalit", "context-user-collapse", expanded, () => SetSessionExpanded(id, false)));
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var (action, text) in new[] { (UserSessionAction.Disconnect, "Odpojit"), (UserSessionAction.Logoff, "Odhlásit se"), (UserSessionAction.Message, "Odeslat zprávu…") }) {
            bool enabled = !userActionBusy && SelectedUserSession() is { } session && UserSessionText.CanRun(action, session.State);
            menu.Items.Add(ContextItem(text, "context-user-" + action.ToString().ToLowerInvariant(), enabled, async () => await RunUserActionAsync(action)));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(ContextItem("Spravovat uživatelské účty", "context-user-accounts", true, OpenUserAccounts));
        ShowContextMenu(menu, owner, args);
    }

    private async Task RunUserActionAsync(UserSessionAction action)
    {
        if (userActionBusy || SelectedUserSession() is not { } session || !UserSessionText.CanRun(action, session.State)) return;
        userActionBusy = true; RefreshUserActions();
        try {
            string title = "", text = "";
            if (action == UserSessionAction.Message) {
                if (await AskUserMessageAsync() is not { } message) return;
                (title, text) = message;
            } else if (!await ConfirmUserActionAsync(action)) return;
            // The session id may have been reused by another logon while the dialog was open.
            if (!model.Sessions.Any(current => current.Id == session.Id && current.User == session.User && current.Domain == session.Domain)) {
                await ShowUserActionResultAsync("Relace se mezitím změnila nebo skončila. Akce nebyla provedena.");
                return;
            }
            uint id = session.Id;
            int error = await Task.Run(() => UserSessionApi.Run(action, id, title, text));
            NativeFrame.InvalidateSessions();
            if (error != 0) await ShowUserActionResultAsync(UserSessionText.Failure(action, UserSessionText.Account(session.User, session.Domain), id, error));
        } finally {
            userActionBusy = false; RefreshUserActions();
        }
    }

    private async Task<bool> ConfirmUserActionAsync(UserSessionAction action)
    {
        var (title, content, confirm) = UserSessionText.Confirmation(action);
        var dialog = Appearance.Dialog(new ContentDialog {
            Title = title, XamlRoot = root.XamlRoot, PrimaryButtonText = confirm, CloseButtonText = "Zrušit", DefaultButton = ContentDialogButton.Close
        });
        if (content.Length > 0) dialog.Content = ServiceActionText(content, 0xe9eef0);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<(string Title, string Text)?> AskUserMessageAsync()
    {
        var title = new TextBox { Header = "Nadpis zprávy:", Text = UserSessionText.MessageTitle(Environment.UserName, DateTime.Now), FontSize = 12 };
        var text = new TextBox { Header = "Zpráva:", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120, FontSize = 12 };
        var body = new StackPanel { Spacing = 12, MinWidth = 340 };
        body.Children.Add(title); body.Children.Add(text);
        var dialog = Appearance.Dialog(new ContentDialog {
            Title = "Odeslat zprávu", XamlRoot = root.XamlRoot, Content = body, PrimaryButtonText = "OK", CloseButtonText = "Zrušit",
            DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false
        });
        text.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = text.Text.Length > 0;
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? (title.Text, text.Text) : null;
    }

    private async Task ShowUserActionResultAsync(string message)
    {
        await Appearance.Dialog(new ContentDialog {
            Title = "Uživatelé", XamlRoot = root.XamlRoot, Content = ServiceActionText(message, 0xe9eef0), CloseButtonText = "Zavřít", DefaultButton = ContentDialogButton.Close
        }).ShowAsync();
    }

    private void OpenUserAccounts()
    {
        // Taskmgr.exe references only the Control Panel canonical name Microsoft.UserAccounts, no ms-settings page.
        try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "control.exe"), "/name Microsoft.UserAccounts") { UseShellExecute = false })?.Dispose(); }
        catch (Win32Exception error) { _ = ShowUserActionResultAsync("Nastavení uživatelských účtů se nepodařilo otevřít. " + error.Message); }
    }

    private string FullName(UserSession session)
    {
        string key = UserSessionText.Account(session.User, session.Domain);
        if (fullNames.TryGetValue(key, out var name)) return name;
        if (fullNamesPending.Add(key)) Load(session.User, session.Domain);
        return "";

        async void Load(string user, string domain)
        {
            string value = await Task.Run(() => UserSessionApi.FullName(user, domain));
            fullNamesPending.Remove(key); fullNames[key] = value;
            if (view == DesktopView.Users) RefreshUsers();
        }
    }
}

/// <summary>WTS and account calls of the Users view. Actions run with the caller's token; there is no elevated path.</summary>
internal static unsafe partial class UserSessionApi
{
    private const uint SessionChange = 0x02B1, AllSessions = 1, IconInformation = 0x40;
    private const int NameDisplay = 3, DomainJoined = 3;
    private const nuint SubclassId = 0x4E4B5355;

    /// <summary>Subclasses the window so that WM_WTSSESSION_CHANGE invalidates the native session cache.</summary>
    internal static bool Watch(nint window)
    {
        if (!SetWindowSubclass(window, &Procedure, SubclassId, 0)) return false;
        if (WTSRegisterSessionNotification(window, AllSessions)) return true;
        RemoveWindowSubclass(window, &Procedure, SubclassId);
        return false;
    }

    internal static void Unwatch(nint window)
    {
        WTSUnRegisterSessionNotification(window);
        RemoveWindowSubclass(window, &Procedure, SubclassId);
    }

    [UnmanagedCallersOnly]
    private static nint Procedure(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == SessionChange) NativeFrame.InvalidateSessions();
        return DefSubclassProc(window, message, wParam, lParam);
    }

    /// <summary>Returns 0 or the Win32 error. Logoff and disconnect do not wait for completion.</summary>
    internal static int Run(UserSessionAction action, uint session, string title, string message)
    {
        bool done = action switch {
            UserSessionAction.Disconnect => WTSDisconnectSession(0, session, false),
            UserSessionAction.Logoff => WTSLogoffSession(0, session, false),
            _ => WTSSendMessage(0, session, title, (uint)title.Length * 2, message, (uint)message.Length * 2, IconInformation, 0, out _, false)
        };
        return done ? 0 : Marshal.GetLastPInvokeError();
    }

    /// <summary>The account's full name, or empty when it is unknown or not readable.</summary>
    internal static string FullName(string user, string domain)
    {
        if (string.Equals(user, Environment.UserName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(domain, Environment.UserDomainName, StringComparison.OrdinalIgnoreCase)) {
            char* buffer = stackalloc char[256];
            uint size = 256;
            if (GetUserNameEx(NameDisplay, buffer, ref size) && size > 0) return new string(buffer, 0, (int)size);
        }
        // Without a server NetUserGetInfo reads only local accounts; a domain account on a joined PC is not among them.
        if (!string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) {
            if (NetGetJoinInformation(null, out nint joined, out int status) != 0) return "";
            NetApiBufferFree(joined);
            if (status == DomainJoined) return "";
        }
        if (NetUserGetInfo(null, user, 23, out nint info) != 0) return "";
        try { return Marshal.PtrToStringUni(Marshal.ReadIntPtr(info, IntPtr.Size)) ?? ""; }
        finally { NetApiBufferFree(info); }
    }

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint window, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> procedure, nuint id, nuint data);
    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint window, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> procedure, nuint id);
    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(nint window, uint flags);
    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSUnRegisterSessionNotification(nint window);
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSDisconnectSession(nint server, uint session, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSLogoffSession(nint server, uint session, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSSendMessageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSSendMessage(nint server, uint session, string title, uint titleBytes, string message, uint messageBytes,
        uint style, uint timeout, out uint response, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [LibraryImport("secur32.dll", EntryPoint = "GetUserNameExW")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool GetUserNameEx(int format, char* buffer, ref uint size);
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint NetGetJoinInformation(string? server, out nint name, out int status);
    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint NetUserGetInfo(string? server, string user, uint level, out nint buffer);
    [LibraryImport("netapi32.dll")]
    private static partial uint NetApiBufferFree(nint buffer);
}
