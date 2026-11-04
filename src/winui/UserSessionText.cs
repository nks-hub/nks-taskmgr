using System.ComponentModel;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal enum UserSessionAction { Disconnect, Logoff, Message }

/// <summary>Texts and rules of the Users view, worded after the native Task Manager resources.</summary>
internal static class UserSessionText
{
    internal const int AccessDenied = 5;

    internal static string State(SessionConnectState state) => state switch {
        SessionConnectState.Active => "Aktivní", SessionConnectState.Connected => "Připojeno", SessionConnectState.ConnectQuery => "Připojování",
        SessionConnectState.Shadow => "Stínový provoz", SessionConnectState.Disconnected => "Odpojeno", SessionConnectState.Idle => "Nečinná",
        SessionConnectState.Listen => "Naslouchá", SessionConnectState.Reset => "Resetována", SessionConnectState.Down => "Mimo provoz",
        SessionConnectState.Init => "Inicializace", _ => "Neznámý"
    };

    /// <summary>Native row title 37023 „%1 (%2!d!)“: account and process count.</summary>
    internal static string Title(string name, int processes) => $"{name} ({processes})";

    internal static string Account(string user, string domain) => domain.Length > 0 ? domain + "\\" + user : user;

    /// <summary>The user column; with the full account name it adds the account's full name when one is known.</summary>
    internal static string Name(string user, string domain, string fullName, bool showFull) =>
        !showFull ? user : fullName.Length > 0 ? $"{fullName} ({Account(user, domain)})" : Account(user, domain);

    /// <summary>Disconnect only makes sense for a session with a live connection; a message needs someone at the desktop.</summary>
    internal static bool CanRun(UserSessionAction action, SessionConnectState state) => action switch {
        UserSessionAction.Disconnect => state is SessionConnectState.Active or SessionConnectState.Connected,
        UserSessionAction.Message => state == SessionConnectState.Active,
        _ => true
    };

    /// <summary>Native 37220 „Zpráva od %1 – %2“.</summary>
    internal static string MessageTitle(string sender, DateTime time) => $"Zpráva od {sender} – {time:g}";

    /// <summary>Title, explanation and confirm button of the native confirmation dialogs.</summary>
    internal static (string Title, string Content, string Confirm) Confirmation(UserSessionAction action) => action == UserSessionAction.Disconnect
        ? ("Opravdu chcete odpojit vybrané uživatele?", "", "Odpojit uživatele")
        : ("Chcete pokračovat?", "Pokud odhlásíte uživatele, může dojít ke ztrátě jeho neuložených dat.", "Odhlásit uživatele");

    /// <summary>Native 37214 and 37221; access denied adds 37245 and the meaning of 37244.</summary>
    internal static string Failure(UserSessionAction action, string user, uint session, int error)
    {
        string head = action switch {
            UserSessionAction.Disconnect => $"Uživatele {user} (id relace={session}) se nepodařilo odpojit.",
            UserSessionAction.Logoff => $"Uživatele {user} (id relace={session}) se nepodařilo odhlásit.",
            _ => $"Zprávu uživateli (SessionId={session}) nelze odeslat."
        };
        return head + "\n" + (error == AccessDenied
            ? "Přístup byl odepřen. K dokončení této úlohy potřebujete oprávnění správce. Zavřete NKS Task Manager, pak ho spusťte znovu jako správce a zkuste to znovu."
            : $"Windows {error} (0x{error:X8}): {new Win32Exception(error).Message}");
    }

    internal static void Check()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        Require(State(SessionConnectState.Shadow) == "Stínový provoz" && State(SessionConnectState.Disconnected) == "Odpojeno"
            && State((SessionConnectState)99) == "Neznámý", "A session state lost its native wording.");
        Require(Title("jana", 12) == "jana (12)", "The row title does not follow „%1 (%2)“.");
        Require(Name("jan", "PC", "Jan Novák", false) == "jan" && Name("jan", "PC", "Jan Novák", true) == "Jan Novák (PC\\jan)"
            && Name("jan", "PC", "", true) == "PC\\jan" && Name("jan", "", "", true) == "jan", "The full account name toggle is wrong.");
        Require(CanRun(UserSessionAction.Disconnect, SessionConnectState.Active) && CanRun(UserSessionAction.Disconnect, SessionConnectState.Connected)
            && !CanRun(UserSessionAction.Disconnect, SessionConnectState.Disconnected) && !CanRun(UserSessionAction.Disconnect, SessionConnectState.Unknown),
            "Disconnect is offered outside Active and Connected.");
        Require(CanRun(UserSessionAction.Logoff, SessionConnectState.Disconnected) && !CanRun(UserSessionAction.Message, SessionConnectState.Disconnected),
            "Log off or message availability is wrong.");
        Require(MessageTitle("jana", new DateTime(2026, 11, 4, 9, 5, 0)).StartsWith("Zpráva od jana – ", StringComparison.Ordinal), "The message title lost its native format.");
        Require(Confirmation(UserSessionAction.Disconnect) == ("Opravdu chcete odpojit vybrané uživatele?", "", "Odpojit uživatele")
            && Confirmation(UserSessionAction.Logoff).Confirm == "Odhlásit uživatele", "A confirmation text changed.");
        string denied = Failure(UserSessionAction.Disconnect, "PC\\jan", 3, AccessDenied);
        Require(denied.StartsWith("Uživatele PC\\jan (id relace=3) se nepodařilo odpojit.\nPřístup byl odepřen.", StringComparison.Ordinal)
            && denied.Contains("jako správce", StringComparison.Ordinal), "Access denied lost the native wording.");
        Require(Failure(UserSessionAction.Message, "x", 7, 1).StartsWith("Zprávu uživateli (SessionId=7) nelze odeslat.\nWindows 1 (0x00000001)", StringComparison.Ordinal),
            "The message failure text changed.");
    }
}
