using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace NksTaskMgr.WinUI;

internal static partial class WindowOwnership
{
    private const int OwnerWindowIndex = -8;
    private const uint OwnerWindow = 4, PreviousWindow = 3;

    internal static void Set(Window window, Window owner)
    {
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window), parent = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        Marshal.SetLastSystemError(0);
        nint previous = SetWindowLongPtr(handle, OwnerWindowIndex, parent);
        int error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0) throw new Win32Exception(error, Loc.T("Okno se nepodařilo svázat s hlavním oknem."));
        if (GetWindow(handle, OwnerWindow) != parent) throw new InvalidOperationException(Loc.T("Okno nemá ověřené hlavní okno."));
    }

    internal static void Restore(Window window)
    {
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (IsIconic(handle)) {
            ShowWindow(handle, 9);
            if (IsIconic(handle)) throw new InvalidOperationException(Loc.T("Minimalizované okno se nepodařilo obnovit."));
        }
    }

    internal static void Minimize(Window window) => ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(window), 6);
    internal static bool IsMinimized(Window window) => IsIconic(WinRT.Interop.WindowNative.GetWindowHandle(window));

    internal static bool IsOwnedBy(Window window, Window owner)
        => GetWindow(WinRT.Interop.WindowNative.GetWindowHandle(window), OwnerWindow) == WinRT.Interop.WindowNative.GetWindowHandle(owner);

    internal static bool IsAboveOwner(Window window, Window owner)
    {
        nint target = WinRT.Interop.WindowNative.GetWindowHandle(window);
        nint next = GetWindow(WinRT.Interop.WindowNative.GetWindowHandle(owner), PreviousWindow);
        for (int i = 0; next != 0 && i < 4096; ++i) {
            if (next == target) return true;
            next = GetWindow(next, PreviousWindow);
        }
        return false;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint window, int index, nint value);
    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, uint command);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);
}
