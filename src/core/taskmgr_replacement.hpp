#pragma once
#include <windows.h>
#include <span>
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
enum class Location { safe, outside, writable, unreadable };
struct Result {
    Status before{};
    bool changed{};
    /// Disable deleted Debugger because the backed-up program is missing or can be changed by non-administrators.
    bool backupDropped{};
    LSTATUS error{};
};
/// What a change would see; values are empty when absent.
struct Plan {
    Status status{};
    std::wstring debugger, backup;
    bool hasBackup{}, backupSafe{};
    LSTATUS error{};
};
/// SID strings allowed to own or modify protected files; empty means SYSTEM, Administrators and TrustedInstaller.
using Trusted = std::span<const std::wstring>;

/// Debugger command for an absolute GUI path; empty when the path is not an absolute NksTaskMgr.WinUI.exe path.
std::wstring debuggerCommand(std::wstring_view guiPath);
/// True when the path lies inside the directory (case-insensitive, no parent traversal).
bool insideDirectory(std::wstring_view path, std::wstring_view directory);
/// Requires a canonical path inside the directory where the file and every directory up to and including the directory
/// are owned by, and writable, deletable or re-permissionable only by, trusted SIDs.
Location machineLocation(std::wstring_view path, std::wstring_view directory, Trusted trusted = {});
/// A null key means the taskmgr.exe IFEO key does not exist.
Status status(HKEY key, std::wstring_view guiPath, LSTATUS* error = nullptr);
Plan plan(HKEY key, std::wstring_view guiPath, Trusted trusted = {});
/// Registers guiPath; a foreign Debugger value is backed up first, an NKS one is replaced without backup.
Result enable(HKEY key, std::wstring_view guiPath);
/// Only while Debugger still starts NksTaskMgr.WinUI.exe: restores the backup when its program is present and protected,
/// otherwise deletes Debugger.
Result disable(HKEY key, std::wstring_view guiPath, Trusted trusted = {});
/// Reads the real HKLM registration; needs no elevation.
Status systemStatus(std::wstring_view guiPath, LSTATUS* error = nullptr);
Plan systemPlan(std::wstring_view guiPath);
/// Changes the real HKLM registration; needs an elevated caller.
Result applySystem(bool enabled, std::wstring_view guiPath);
}
