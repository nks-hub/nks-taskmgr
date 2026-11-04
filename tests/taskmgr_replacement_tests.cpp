#include "core/taskmgr_replacement.hpp"
#include <iostream>
#include <optional>
#include <stdexcept>

namespace {
using namespace nks::replacement;
unsigned cases{};
const std::wstring gui = L"C:\\Program Files\\NKS Task Manager\\NksTaskMgr.WinUI.exe";
const std::wstring quoted = L"\"" + gui + L"\"";
const std::wstring foreign = L"\"C:\\Tools\\procexp64.exe\"";

void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); ++cases; }

// Tests run against a private HKCU key; the real HKLM IFEO key is never opened.
struct TemporaryKey {
    std::wstring path = L"Software\\NKS Task Manager Tests\\IFEO-" + std::to_wstring(GetCurrentProcessId());
    HKEY key{};
    TemporaryKey()
    {
        RegDeleteTreeW(HKEY_CURRENT_USER, path.c_str());
        if (RegCreateKeyExW(HKEY_CURRENT_USER, path.c_str(), 0, nullptr, REG_OPTION_VOLATILE, KEY_ALL_ACCESS, nullptr, &key, nullptr))
            throw std::runtime_error("Temporary registry key was not created");
    }
    ~TemporaryKey()
    {
        RegCloseKey(key); RegDeleteTreeW(HKEY_CURRENT_USER, path.c_str());
        RegDeleteKeyW(HKEY_CURRENT_USER, L"Software\\NKS Task Manager Tests"); // fails harmlessly while another run still uses it
    }
};

std::optional<std::wstring> value(HKEY key, const wchar_t* name, DWORD* type = nullptr)
{
    wchar_t buffer[512]{}; DWORD size = sizeof(buffer) - sizeof(wchar_t), kind = 0;
    if (RegQueryValueExW(key, name, nullptr, &kind, reinterpret_cast<BYTE*>(buffer), &size)) return std::nullopt;
    if (type) *type = kind;
    return std::wstring(buffer);
}
void set(HKEY key, const wchar_t* name, const std::wstring& text, DWORD type = REG_SZ)
{
    if (RegSetValueExW(key, name, 0, type, reinterpret_cast<const BYTE*>(text.c_str()), static_cast<DWORD>((text.size() + 1) * sizeof(wchar_t))))
        throw std::runtime_error("Test value was not written");
}
bool ok(const Result& result, Status before, bool changed) { return !result.error && result.before == before && result.changed == changed; }

void paths()
{
    require(debuggerCommand(gui) == quoted, "Path with spaces was not quoted");
    require(debuggerCommand(L"D:\\nks\\nkstaskmgr.winui.EXE") == L"\"D:\\nks\\nkstaskmgr.winui.EXE\"", "Image name was case-sensitive");
    for (const auto* bad : {L"NksTaskMgr.WinUI.exe", L"\\\\server\\share\\NksTaskMgr.WinUI.exe", L"C:\\a\"b\\NksTaskMgr.WinUI.exe",
        L"C:\\Tools\\other.exe", L"C:\\a\\..\\NksTaskMgr.WinUI.exe", L"C:/a/NksTaskMgr.WinUI.exe", L""})
        require(debuggerCommand(bad).empty(), "An unsafe GUI path was accepted");
    require(insideDirectory(gui, L"C:\\Program Files"), "Program Files install was refused");
    require(insideDirectory(gui, L"c:\\program files\\"), "Program Files comparison was case-sensitive");
    require(!insideDirectory(L"C:\\Program Files (x86)\\NKS\\NksTaskMgr.WinUI.exe", L"C:\\Program Files"), "Sibling directory was accepted");
    require(!insideDirectory(L"C:\\Users\\a\\AppData\\Local\\NKS\\NksTaskMgr.WinUI.exe", L"C:\\Program Files"), "Per-user install was accepted");
    require(!insideDirectory(L"C:\\Program Files\\..\\Users\\NksTaskMgr.WinUI.exe", L"C:\\Program Files"), "Parent traversal was accepted");
    require(status(nullptr, gui) == Status::notRegistered, "A missing IFEO key was not reported as unregistered");
    require(ok(disable(nullptr, gui), Status::notRegistered, false), "Disable without IFEO key changed something");
}

void lifecycle()
{
    TemporaryKey temp; const HKEY key = temp.key;
    // Enable with no previous value, then restore to nothing.
    require(ok(enable(key, gui), Status::notRegistered, true), "First enable failed");
    require(value(key, debuggerValue) == quoted && !value(key, backupValue), "Enable without a previous value wrote a backup");
    require(status(key, gui) == Status::registered, "Own registration was not recognized");
    require(ok(disable(key, gui), Status::registered, true), "Disable failed");
    require(!value(key, debuggerValue) && !value(key, backupValue), "Disable left values behind");

    // Enable over a foreign replacement, twice; the backup keeps the foreign value.
    set(key, debuggerValue, foreign);
    require(status(key, gui) == Status::foreign, "Foreign registration was not recognized");
    require(ok(enable(key, gui), Status::foreign, true), "Enable over a foreign value failed");
    require(value(key, debuggerValue) == quoted && value(key, backupValue) == foreign, "Foreign value was not backed up");
    require(ok(enable(key, gui), Status::registered, false), "Second enable was not idempotent");
    require(value(key, backupValue) == foreign, "Second enable overwrote the backup with its own value");
    require(ok(disable(key, gui), Status::registered, true), "Disable after a foreign backup failed");
    require(value(key, debuggerValue) == foreign && !value(key, backupValue), "Foreign value was not restored");

    // Someone else replaced NKS after it was enabled: disable must not touch anything.
    require(ok(enable(key, gui), Status::foreign, true), "Re-enable failed");
    set(key, debuggerValue, L"C:\\Other\\tool.exe /x");
    require(ok(disable(key, gui), Status::foreign, false), "Disable changed a newer foreign registration");
    require(value(key, debuggerValue) == L"C:\\Other\\tool.exe /x" && value(key, backupValue) == foreign, "Newer foreign registration was modified");
    // Enabling again backs up the newer foreign value instead of the older one.
    require(ok(enable(key, gui), Status::foreign, true), "Enable over a newer foreign value failed");
    require(value(key, backupValue) == L"C:\\Other\\tool.exe /x", "Backup did not follow the newest foreign value");

    // A moved NKS install leaves a stale registration that the new path replaces and can restore.
    set(key, debuggerValue, L"\"D:\\Old place\\NksTaskMgr.WinUI.exe\"");
    require(status(key, gui) == Status::otherNks, "Stale NKS path was not recognized");
    require(ok(enable(key, gui), Status::otherNks, true), "Enable over a stale NKS path failed");
    require(value(key, debuggerValue) == quoted && value(key, backupValue) == L"C:\\Other\\tool.exe /x", "Stale NKS path was backed up");
    set(key, debuggerValue, L"D:\\Old\\NKSTASKMGR.WINUI.EXE");
    require(ok(disable(key, gui), Status::otherNks, true), "Disable from a stale NKS path failed");
    require(value(key, debuggerValue) == L"C:\\Other\\tool.exe /x" && !value(key, backupValue), "Stale disable did not restore the backup");

    // Own path with arguments counts as registered and is rewritten to the canonical form.
    set(key, debuggerValue, L"\"c:\\program files\\nks task manager\\nkstaskmgr.winui.exe\" /x");
    require(status(key, gui) == Status::registered, "Own path with other case and arguments was not recognized");
    require(ok(enable(key, gui), Status::registered, true) && value(key, debuggerValue) == quoted, "Own value was not normalized");
    require(!value(key, backupValue), "Normalizing own value wrote a backup");
    require(ok(disable(key, gui), Status::registered, true), "Disable after normalizing failed");

    // A foreign REG_EXPAND_SZ value keeps its type through backup and restore.
    set(key, debuggerValue, L"%ProgramFiles%\\Tool\\x.exe", REG_EXPAND_SZ);
    require(ok(enable(key, gui), Status::foreign, true), "Enable over REG_EXPAND_SZ failed");
    require(ok(disable(key, gui), Status::registered, true), "Disable over REG_EXPAND_SZ backup failed");
    DWORD type = 0;
    require(value(key, debuggerValue, &type) == L"%ProgramFiles%\\Tool\\x.exe" && type == REG_EXPAND_SZ, "Value type was not restored");
    // Windows runs an unquoted path with spaces as C:\Program, so it is not NKS.
    set(key, debuggerValue, L"C:\\Program Files\\NKS Task Manager\\NksTaskMgr.WinUI.exe");
    require(status(key, gui) == Status::foreign, "Unquoted path with spaces was treated as NKS");
    require(enable(key, L"relative\\NksTaskMgr.WinUI.exe").error == ERROR_INVALID_PARAMETER, "Relative path was registered");
    require(disable(key, L"relative\\NksTaskMgr.WinUI.exe").error == ERROR_INVALID_PARAMETER, "Relative path was used for disable");
}
}

int main()
{
    try {
        paths(); lifecycle();
        std::cout << "taskmgr replacement: " << cases << " checks passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "taskmgr replacement failed: " << error.what() << "\n";
        return 1;
    }
}
