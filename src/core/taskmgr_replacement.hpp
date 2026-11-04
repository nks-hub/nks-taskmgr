#pragma once
#include <windows.h>
#include <string>
#include <string_view>

/// Replacing Windows Task Manager through the Image File Execution Options "Debugger" value of taskmgr.exe.
/// The previous Debugger value is kept as raw type and data in an NKS-named value of the same key.
namespace nks::replacement {
inline constexpr wchar_t systemKey[] = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\taskmgr.exe";
inline constexpr wchar_t debuggerValue[] = L"Debugger";
inline constexpr wchar_t backupValue[] = L"NksTaskMgr.PreviousDebugger";
inline constexpr wchar_t guiImage[] = L"NksTaskMgr.WinUI.exe";

enum class Status { notRegistered, registered, otherNks, foreign };
struct Result {
    Status before{};
    bool changed{};
    LSTATUS error{};
};

/// Debugger command for an absolute GUI path; empty when the path is not an absolute NksTaskMgr.WinUI.exe path.
std::wstring debuggerCommand(std::wstring_view guiPath);
/// True when the path lies inside the given Program Files directory.
bool insideDirectory(std::wstring_view path, std::wstring_view directory);
/// A null key means the taskmgr.exe IFEO key does not exist.
Status status(HKEY key, std::wstring_view guiPath, LSTATUS* error = nullptr);
/// Registers guiPath; a foreign Debugger value is backed up first, an NKS one is replaced without backup.
Result enable(HKEY key, std::wstring_view guiPath);
/// Restores the backed-up value (or removes Debugger) only while the Debugger value still starts NksTaskMgr.WinUI.exe.
Result disable(HKEY key, std::wstring_view guiPath);
/// Reads the real HKLM registration; needs no elevation.
Status systemStatus(std::wstring_view guiPath, LSTATUS* error = nullptr);
/// Changes the real HKLM registration; needs an elevated caller.
Result applySystem(bool enabled, std::wstring_view guiPath);
}
