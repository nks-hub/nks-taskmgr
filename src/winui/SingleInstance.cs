using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Dispatching;

namespace NksTaskMgr.WinUI;

/// <param name="Independent">True for QA and diagnostic launches, which never join the single GUI instance.</param>
/// <param name="View">Requested start view, or null for the default.</param>
internal readonly record struct LaunchRequest(bool Independent, DesktopView? View);

internal static class LaunchArguments
{
    // Keep in sync with the QA and diagnostic switches read in App and MainWindow.
    private static readonly string[] IndependentPrefixes = ["--smoke=", "--bench-sort", "--check-", "--profile-ticks=", "--diagnose-gaps"];

    /// <summary>The command line this process honors; every reader of switches uses this instead of the raw arguments.</summary>
    internal static string[] Current { get; } = Filter(Environment.GetCommandLineArgs(), IsElevated());

    /// <summary>
    /// Drops everything from a taskmgr.exe path onward, because Image File Execution Options passes the arguments of
    /// whoever started Task Manager. In an elevated process it also drops every QA and diagnostic switch: taskmgr.exe
    /// auto-elevates, so a standard user could otherwise choose the files an administrator instance writes.
    /// </summary>
    internal static string[] Filter(string[] args, bool elevated)
    {
        var kept = new List<string>(args.Length);
        for (int i = 0; i < args.Length; ++i) {
            if (i > 0 && string.Equals(Path.GetFileName(args[i]), "taskmgr.exe", StringComparison.OrdinalIgnoreCase)) break;
            if (i > 0 && elevated && IndependentPrefixes.Any(prefix => args[i].StartsWith(prefix, StringComparison.Ordinal))) continue;
            kept.Add(args[i]);
        }
        return [.. kept];
    }

    /// <summary>The value of the first <paramref name="prefix"/> switch, or null.</summary>
    internal static string? Value(string[] args, string prefix) =>
        args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Parses the process command line (element 0 is the image). When Image File Execution Options starts the app,
    /// element 1 is the original Task Manager path and everything after it belongs to Task Manager and is ignored.
    /// Unrecognized arguments are ignored.
    /// </summary>
    internal static LaunchRequest Parse(string[] args)
    {
        bool independent = false;
        DesktopView? view = null;
        for (int i = 1; i < args.Length; ++i) {
            string arg = args[i];
            if (i == 1 && string.Equals(Path.GetFileName(arg), "taskmgr.exe", StringComparison.OrdinalIgnoreCase)) break;
            if (arg.StartsWith("--view=", StringComparison.OrdinalIgnoreCase))
                view ??= arg[7..].ToLowerInvariant() switch {
                    "services" => DesktopView.Services, "applications" => DesktopView.Applications, "processes" => DesktopView.Processes, "users" => DesktopView.Users, _ => null };
            else if (IndependentPrefixes.Any(prefix => arg.StartsWith(prefix, StringComparison.Ordinal))) independent = true;
        }
        return new LaunchRequest(independent, view);
    }

    internal static void Check()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        const string taskmgr = @"C:\Windows\System32\Taskmgr.exe";
        Require(Parse(["gui"]) == new LaunchRequest(false, null), "Plain launch changed.");
        Require(Parse(["gui", taskmgr]) == new LaunchRequest(false, null), "Image File Execution Options path was not ignored.");
        Require(Parse(["gui", taskmgr, "/0", "/startup", "/7"]) == new LaunchRequest(false, null), "Task Manager arguments were not ignored.");
        Require(Parse(["gui", "taskmgr.EXE", "--smoke=a", "--view=Services"]) == new LaunchRequest(false, null), "Arguments after the Task Manager path were honored.");
        Require(Parse(["gui", "--view=services"]) == new LaunchRequest(false, DesktopView.Services), "The Services view was not parsed.");
        Require(Parse(["gui", "--view=users"]) == new LaunchRequest(false, DesktopView.Users), "The Users view was not parsed.");
        Require(Parse(["gui", "--view=APPLICATIONS", "--view=Processes"]) == new LaunchRequest(false, DesktopView.Applications), "The first view did not win.");
        Require(Parse(["gui", "--view=Processes"]).View == DesktopView.Processes, "The Processes view was not parsed.");
        Require(Parse(["gui", "--view=Bogus", "--view=", "--view=1", "/7", "--unknown", "--unknown=x", ""]) == new LaunchRequest(false, null), "Unknown arguments changed behavior.");
        foreach (string qa in new[] { "--smoke=a", "--bench-sort=a", "--bench-sort-no-icons", "--check-events=a", "--check-actions=a", "--check-dump=a",
            "--check-applications=a", "--check-details=a", "--profile-ticks=a", "--diagnose-gaps=a" })
            Require(Parse(["gui", qa]).Independent && Parse(["gui", "--view=Services", qa]).Independent, "A QA switch joined the single instance: " + qa);
        Require(Value(Filter(["gui", taskmgr, "--check-dump=x"], false), "--check-dump=") is null, "A switch after the Task Manager path was honored.");
        Require(Filter(["gui", "--view=services", "taskmgr.exe", "--view=processes"], false) is ["gui", "--view=services"], "Arguments after a later taskmgr.exe were kept.");
        Require(Value(Filter(["gui", "--check-dump=x"], false), "--check-dump=") == "x", "A standard QA launch lost its switch.");
        Require(Filter(["gui", "--check-dump=x", "--smoke=y", "--bench-sort-no-icons", "--profile-ticks=z", "--diagnose-gaps=w", "--view=services"], true)
            is ["gui", "--view=services"], "An elevated process kept a QA or diagnostic switch.");
        Require(!Parse(Filter(["gui", "--check-dump=x"], true)).Independent, "An elevated QA launch stayed independent.");
    }
}

/// <summary>Allows one GUI instance per user, elevation level and logon session.</summary>
internal static partial class SingleInstance
{
    private static EventWaitHandle? signal;
    private static RegisteredWaitHandle? registration;

    /// <summary>Returns true for the first instance; otherwise asks it to activate its window and returns false.</summary>
    internal static bool TryBecomePrimary()
    {
        try {
            using var identity = WindowsIdentity.GetCurrent();
            bool elevated = LaunchArguments.IsElevated();
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\NksTaskMgr.Gui.{identity.User?.Value}.{(elevated ? "admin" : "user")}", out bool created);
            if (created) { signal = handle; return true; }
            using (handle) {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                foreach (var other in System.Diagnostics.Process.GetProcessesByName(self.ProcessName)) {
                    using (other) if (other.Id != self.Id && other.SessionId == self.SessionId) AllowSetForegroundWindow(other.Id);
                }
                handle.Set();
            }
            return false;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException or System.ComponentModel.Win32Exception) {
            return true;
        }
    }

    /// <summary>Runs <paramref name="activate"/> on the UI thread whenever a later launch signals this instance.</summary>
    internal static void Listen(DispatcherQueue queue, Action activate)
    {
        if (signal is { } handle)
            registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => queue.TryEnqueue(() => activate()), null, Timeout.Infinite, false);
    }

    /// <summary>Releases the instance name so a launch during shutdown starts a new instance.</summary>
    internal static void Stop()
    {
        registration?.Unregister(null);
        registration = null;
        signal?.Dispose();
        signal = null;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
