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
    // Columns follow Task Manager (Uživatel, Stav, CPU, Paměť, Disk, Síť); ID, station and client stay in the row tooltip.
    private readonly Button usersNav = Button("Uživatelé");
    private readonly Grid usersPage = new() { Visibility = Visibility.Collapsed };
    private readonly ListView usersList = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0) };
    private readonly GroupLines userItems = [];
    private readonly GroupTemplates groupTemplates = new();
    private Grid usersHeader = new();
    private readonly List<(Button Button, TextBlock Text)> userHeaders = [];
    private readonly TextBlock usersStatus = Label("Načítám relace…", 11, 0x95a4ad);
    private readonly Button disconnectUser = Button("Odpojit"), logoffUser = Button("Odhlásit"), messageUser = Button("Zpráva…"),
        usersOptions = Button("⋯");
    private readonly ToggleMenuFlyoutItem fullAccountName = new() { Text = "Zobrazit celý název účtu" };
    private readonly HashSet<uint> expandedSessions = [];
    private readonly Dictionary<uint, GroupLine> sessionItems = [];
    private readonly Dictionary<ProcessKey, GroupLine> userProcessItems = [];
    private readonly Dictionary<string, string> fullNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> fullNamesPending = new(StringComparer.OrdinalIgnoreCase);
    private uint? selectedSession;
    private bool userActionBusy, updatingUserSelection, userNetworkShown = true;
    private int userSortColumn;
    private bool userSortDescending;

    private void BuildUsers()
    {
        usersNav.Click += (_, _) => ShowView(DesktopView.Users);
        usersPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        usersPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        usersPage.RowDefinitions.Add(new RowDefinition());
        var toolbar = new Grid { MinHeight = 34, Margin = new Thickness(0, 0, 0, 6), ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        usersStatus.VerticalAlignment = VerticalAlignment.Center; usersStatus.TextTrimming = TextTrimming.CharacterEllipsis; toolbar.Children.Add(usersStatus);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        disconnectUser.Click += async (_, _) => await RunUserActionAsync(UserSessionAction.Disconnect);
        logoffUser.Click += async (_, _) => await RunUserActionAsync(UserSessionAction.Logoff);
        messageUser.Click += async (_, _) => await RunUserActionAsync(UserSessionAction.Message);
        ToolTipService.SetToolTip(disconnectUser, "Odpojit vybraného uživatele. Jeho relace a programy dál běží.");
        ToolTipService.SetToolTip(logoffUser, "Odhlásit vybraného uživatele. Neuložená data jeho programů se mohou ztratit.");
        ToolTipService.SetToolTip(messageUser, "Zobrazit zprávu na ploše vybraného uživatele.");
        AutomationProperties.SetName(usersOptions, "Další možnosti uživatelů");
        var options = new MenuFlyout();
        options.Items.Add(ContextItem("Rozbalit vše", "users-expand-all", true, () => { foreach (var session in model.Sessions) expandedSessions.Add(session.Id); RefreshUsers(); }));
        options.Items.Add(ContextItem("Sbalit vše", "users-collapse-all", true, () => { expandedSessions.Clear(); RefreshUsers(); }));
        options.Items.Add(new MenuFlyoutSeparator());
        fullAccountName.Click += (_, _) => RefreshUsers();
        options.Items.Add(fullAccountName);
        options.Items.Add(ContextItem("Spravovat uživatelské účty", "users-accounts", true, OpenUserAccounts));
        usersOptions.Flyout = options;
        foreach (var button in new[] { disconnectUser, logoffUser, messageUser, usersOptions }) { button.Height = 28; actions.Children.Add(button); }
        Grid.SetColumn(actions, 1); toolbar.Children.Add(actions);
        usersPage.Children.Add(toolbar);

        string[] tips = [
            "Přihlášený uživatel a počet jeho procesů. Šipka nebo dvojklik rozbalí procesy.", "Stav relace Windows.",
            "Součet CPU procesů. ≥ označuje známou část, když některý proces nemá aktuální vzorek.",
            "Součet pracovních sad procesů. Přesné hodnoty v MB jsou v popisku buňky.",
            "Součet čtení a zápisu všech I/O procesů (soubory, zařízení, síťové soubory).",
            "Odeslané a přijaté bajty TCP/UDP. Měří se jen během vlastního záznamu ETW se správcem."];
        usersHeader = GroupHeader("Uživatel", tips, userHeaders, column => {
            userSortDescending = userSortColumn == column ? !userSortDescending : column >= 2; userSortColumn = column; RefreshUsers();
        });
        Grid.SetRow(usersHeader, 1); usersPage.Children.Add(usersHeader);

        ConfigureGroupList(usersList, userItems, () => userNetworkShown);
        usersList.SelectionChanged += (_, _) => {
            if (updatingUserSelection || usersList.SelectedItem is not GroupLine line) return;
            selectedSession = line.Group is uint id ? id : null; RefreshUserActions();
        };
        usersList.DoubleTapped += (_, args) => { if (LineFrom(args.OriginalSource) is { IsGroup: true } line) ToggleUserGroup(line); };
        usersList.PreviewKeyDown += (_, args) => {
            if (args.Key is not (VirtualKey.Left or VirtualKey.Right) || usersList.SelectedItem is not GroupLine { Group: uint id }) return;
            SetSessionExpanded(id, args.Key == VirtualKey.Right); args.Handled = true;
        };
        usersList.ContextRequested += (_, args) => {
            if (LineFrom(args.OriginalSource) is not { Group: uint id } line || usersList.ContainerFromItem(line) is not FrameworkElement owner) return;
            UserContextRequested(id, owner, args);
        };
        AutomationProperties.SetName(usersList, "Uživatelé a jejich procesy"); AutomationProperties.SetAutomationId(usersList, "users-list");
        Grid.SetRow(usersList, 2); usersPage.Children.Add(usersList);
        page.Children.Add(usersPage);
        model.SessionNotifications = UserSessionApi.Watch(windowHandle);
        Closed += (_, _) => { if (model.SessionNotifications) UserSessionApi.Unwatch(windowHandle); };
        RefreshUserActions();
    }

    private static GroupLine? LineFrom(object source) => (source as FrameworkElement)?.DataContext as GroupLine;

    private void ToggleUserGroup(GroupLine line)
    {
        if (line.Group is not uint id) return;
        selectedSession = id; SetSessionExpanded(id, !expandedSessions.Contains(id));
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

    private static string Plural(int count, string one, string few, string many) => count + " " + (count == 1 ? one : count is >= 2 and <= 4 ? few : many);

    internal static string MemoryTip(string label, ulong working, ulong privateBytes)
        => $"{label}: {Display.Number(working / 1_000_000d, " MB")} ({Display.Bytes(working)})\nPrivátní paměť: {Display.Number(privateBytes / 1_000_000d, " MB")} ({Display.Bytes(privateBytes)})";

    private void RefreshUsers()
    {
        var sessions = model.Sessions;
        int processes = sessions.Sum(session => session.Members);
        usersStatus.Text = model.SessionState switch {
            SampleState.First => "Načítám relace…",
            SampleState.Valid => Plural(sessions.Count, "relace", "relace", "relací") + " · " + Plural(processes, "proces", "procesy", "procesů"),
            SampleState.AccessDenied => "Seznam relací Windows vyžaduje oprávnění; relace jsou odvozené jen z procesů.",
            _ => "Seznam relací Windows je nedostupný; relace jsou odvozené jen z procesů."
        } + (model.Stale ? " · údaje jsou zastaralé" : "");
        // Without a running ETW capture there is no per-process network figure, so the column is hidden instead of showing dashes.
        bool network = model.NetworkAvailable;
        if (network != userNetworkShown) {
            userNetworkShown = network;
            usersHeader.ColumnDefinitions[^1].Width = new GridLength(network ? GroupColumns[^1] : 0);
            userItems.Sync([]);
        }
        UpdateGroupHeader(userHeaders, "Uživatel", userSortColumn, userSortDescending);
        var members = new Dictionary<uint, List<ProcessRow>>();
        var sessionNetwork = new Dictionary<uint, (ProcessBranchMetric Sum, int Count)>();
        foreach (var row in model.Processes) {
            if (row.Key.Pid == 0 || !model.ProcessSessions.TryGetValue(row.Key, out uint id)) continue;
            var (sum, count) = sessionNetwork.GetValueOrDefault(id);
            sessionNetwork[id] = (sum.Add(row.Ended || row.Stale ? default : row.Network), count + 1);
            if (!expandedSessions.Contains(id)) continue;
            if (!members.TryGetValue(id, out var list)) members.Add(id, list = []);
            list.Add(row);
        }
        var groups = new List<GroupLine>();
        foreach (var session in sessions) groups.Add(SessionLine(session, sessionNetwork.GetValueOrDefault(session.Id)));
        groups.Sort((a, b) => GroupLine.Compare(a, b, userSortColumn, userSortDescending));
        var wanted = new List<GroupLine>();
        var liveProcesses = new HashSet<ProcessKey>();
        foreach (var group in groups) {
            wanted.Add(group);
            uint id = (uint)group.Group;
            if (!members.TryGetValue(id, out var rows)) continue;
            var lines = rows.Select(row => { liveProcesses.Add(row.Key); return UserProcessLine(id, row); }).ToList();
            lines.Sort((a, b) => GroupLine.Compare(a, b, userSortColumn, userSortDescending));
            wanted.AddRange(lines);
        }
        foreach (var id in sessionItems.Keys.Where(id => !sessions.Any(session => session.Id == id)).ToArray()) sessionItems.Remove(id);
        foreach (var key in userProcessItems.Keys.Where(key => !liveProcesses.Contains(key)).ToArray()) userProcessItems.Remove(key);
        expandedSessions.RemoveWhere(id => !sessionItems.ContainsKey(id));
        if (selectedSession is { } selected && !sessionItems.ContainsKey(selected)) selectedSession = null;
        updatingUserSelection = true;
        try {
            userItems.Sync(wanted);
            object? selection = selectedSession is { } current ? sessionItems.GetValueOrDefault(current) : null;
            if (usersList.SelectedItem is GroupLine { Group: uint group } && group == selectedSession) selection = usersList.SelectedItem;
            if (!ReferenceEquals(usersList.SelectedItem, selection)) usersList.SelectedItem = selection;
        } finally { updatingUserSelection = false; }
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

    private GroupLine SessionLine(UserSession session, (ProcessBranchMetric Sum, int Count) network)
    {
        if (!sessionItems.TryGetValue(session.Id, out var line)) {
            sessionItems.Add(session.Id, line = new GroupLine(session.Id, null, null) { Toggle = ToggleUserGroup });
        }
        bool expanded = expandedSessions.Contains(session.Id);
        bool showFull = fullAccountName.IsChecked;
        string name = session.User.Length > 0 ? UserSessionText.Name(session.User, session.Domain, showFull ? FullName(session) : "", showFull)
            : session.InfoState == SampleState.AccessDenied ? "Nedostupné bez správce"
            : session.Id == 0 ? "Služby systému" : session.Station.Length > 0 ? session.Station : "Neznámý uživatel";
        string protocol = session.Protocol switch {
            SessionProtocol.Console => "konzole", SessionProtocol.Rdp => "RDP", SessionProtocol.Ica => "ICA",
            _ => session.InfoState == SampleState.AccessDenied ? "nedostupné" : "neznámé"
        };
        string state = UserSessionText.State(session.State);
        var (traffic, trafficMembers) = network;
        string account = session.User.Length == 0 ? name : UserSessionText.Account(session.User, session.Domain);
        string tip = $"{account} · relace {session.Id} · {protocol}" + (session.Station.Length > 0 ? $" · stanice {session.Station}" : "")
            + (session.Client.Length > 0 ? $" · klient {session.Client}" : "")
            + (session.InfoState == SampleState.AccessDenied ? "\nÚdaje o uživateli této relace vyžadují oprávnění správce." : "")
            + $"\nZnámé aktuální vzorky: CPU {session.Cpu.Known}/{session.Members}, paměť {session.Working.Known}/{session.Members}, I/O {session.Io.Known}/{session.Members}."
            + "\n≥ označuje známou část neúplného součtu. Sdílené stránky RAM mohou být započteny opakovaně.";
        line.Set(expanded ? "⌄" : "›", UserSessionText.Title(name, session.Members), state,
            session.Cpu.Text(session.Members, false, " %"),
            session.Working.Known == 0 ? "Měření nedostupné" : (session.Working.Known != session.Members ? "≥ " : "") + Display.Bytes(session.Working.Value),
            MemoryTip("Pracovní sada", session.Working.Value, session.Private.Value),
            session.Io.Text(session.Members, false, " MiB/s", 1048576), traffic.NetworkText(trafficMembers, trafficMembers != session.Members), tip,
            $"{name}, {state}, relace {session.Id}, {Plural(session.Members, "proces", "procesy", "procesů")}, {(expanded ? "rozbaleno" : "sbaleno")}");
        line.SetSort(name, session.Cpu.SortValue, session.Working.SortValue, session.Io.SortValue, traffic.SortValue);
        return line;
    }

    private GroupLine UserProcessLine(uint session, ProcessRow row)
    {
        if (!userProcessItems.TryGetValue(row.Key, out var line) || !ReferenceEquals(line.Process, row)) {
            userProcessItems[row.Key] = line = new GroupLine(session, row, row);
        }
        string disk = DiskText(row, out double diskValue);
        line.Set("", row.Name, "", row.CpuText, Display.Bytes(row.WorkingBytes), MemoryTip("Pracovní sada", row.WorkingBytes, row.PrivateBytes),
            disk, row.NetworkText, $"{row.Name} · PID {row.PidText}", $"{row.Name}, PID {row.PidText}");
        line.SetSort(row.Name, row.Cpu.State == SampleState.Valid ? row.Cpu.Value : -1, row.WorkingBytes,
            diskValue, row.Network.SortValue);
        return line;
    }

    private void UserContextRequested(uint id, FrameworkElement owner, ContextRequestedEventArgs args)
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
