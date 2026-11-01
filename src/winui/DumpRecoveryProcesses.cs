using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal static partial class DumpRecoveryProcesses
{
    internal static string UserSid { get { using var current = WindowsIdentity.GetCurrent(); return current.User?.Value ?? throw new IOException("Uživatelskou identitu nelze ověřit."); } }
    internal static DumpCreator Current()
    {
        using var process = OpenProcess(0x101000, false, (uint)Environment.ProcessId);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return Identity(process);
    }
    internal static DumpCreator Identity(SafeProcessHandle process)
    {
        var identity = NativeMonitor.QueryProcessIdentity(process, GenerationKind.CreationTime);
        if (identity.State != SampleState.Valid || identity.Boot.IsEmpty || identity.Generation == 0 || WaitForSingleObject(process, 0) != 258)
            throw new IOException("Identita běžícího procesu není ověřitelná.");
        return new(identity.Boot, identity.Pid, identity.Generation);
    }
    internal static void EnsureStopped(DumpRecoveryRecord record)
    {
        var currentBoot = Current().Boot;
        RequireStopped(record.Creator, currentBoot);
        if (record.Helper is { } helper) RequireStopped(helper, currentBoot);
        else if ((uint)record.Phase >= (uint)DumpRecoveryPhase.StartingHelper) throw new IOException("Identita původního pracovníka není uložená. Soubor zůstane zachovaný.");
    }
    private static void RequireStopped(DumpCreator creator, BootIdentifier currentBoot)
    {
        if (creator.Boot != currentBoot) return;
        using var process = OpenProcess(0x101000, false, creator.Pid);
        if (process.IsInvalid) {
            int error = Marshal.GetLastPInvokeError();
            if (error is 87 or 1168) return;
            throw new IOException("Stav původního procesu nelze ověřit (Windows " + error + "). Soubor zůstane zachovaný.");
        }
        uint wait = WaitForSingleObject(process, 0);
        if (wait == 0) return;
        if (wait != 258) throw new IOException("Životní stav původního procesu není dostupný. Soubor zůstane zachovaný.");
        DumpCreator actual;
        try { actual = Identity(process); }
        catch (Exception error) when (error is NativeMonitorException or Win32Exception) { throw new IOException("Původní proces nelze spolehlivě ověřit. Soubor zůstane zachovaný.", error); }
        if (actual.Boot == creator.Boot && actual.Created100ns == creator.Created100ns)
            throw new IOException("Původní aplikace nebo pracovník stále běží. Soubor se nebude měnit.");
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
