#include "core/taskmgr_replacement.hpp"
#include <aclapi.h>
#include <sddl.h>
#include <filesystem>
#include <iostream>
#include <optional>
#include <stdexcept>
#include <vector>

namespace {
using namespace nks::replacement;
unsigned cases{};
const std::wstring gui = L"C:\\Program Files\\NKS Task Manager\\NksTaskMgr.WinUI.exe";
const std::wstring quoted = L"\"" + gui + L"\"";

void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); ++cases; }

std::wstring currentUser()
{
    HANDLE token = nullptr; std::vector<BYTE> buffer(512); DWORD size = 0; LPWSTR text = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) throw std::runtime_error("Token was not opened");
    const bool read = GetTokenInformation(token, TokenUser, buffer.data(), static_cast<DWORD>(buffer.size()), &size);
    CloseHandle(token);
    if (!read || !ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &text)) throw std::runtime_error("User SID was not read");
    std::wstring result(text); LocalFree(text); return result;
}
const std::wstring user = currentUser();
// Owner and writers of the controlled test trees; Guests stands in for any non-administrator.
const std::vector<std::wstring> trustedTree{user, L"S-1-5-18", L"S-1-5-32-544"};
const std::wstring protectedAcl = L"D:P(A;;FA;;;" + user + L")(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)";

void create(const std::wstring& path, const std::wstring& sddl, bool directory)
{
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr))
        throw std::runtime_error("Test security descriptor is invalid");
    SECURITY_ATTRIBUTES attributes{sizeof(attributes), descriptor, FALSE};
    bool created = directory ? CreateDirectoryW(path.c_str(), &attributes) != FALSE : false;
    if (!directory) {
        const HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, &attributes, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        created = file != INVALID_HANDLE_VALUE; if (created) CloseHandle(file);
    }
    LocalFree(descriptor);
    if (!created) throw std::runtime_error("Test file system object was not created");
}

// Restoring a backup checks the whole path up to the volume root. The directories above the temporary folder are not
// under the test's control, so every owner and allowed SID found there is trusted; the tests vary only objects below it.
std::vector<std::wstring> trustedAbove(std::filesystem::path directory)
{
    std::vector<std::wstring> result = trustedTree;
    auto add = [&](PSID sid) {
        LPWSTR text = nullptr;
        if (!sid || !ConvertSidToStringSidW(sid, &text)) return;
        result.emplace_back(text); LocalFree(text);
    };
    for (; directory.has_relative_path(); directory = directory.parent_path()) {
        PSID owner = nullptr; PACL dacl = nullptr; PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (GetNamedSecurityInfoW(directory.c_str(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
            &owner, nullptr, &dacl, nullptr, &descriptor)) throw std::runtime_error("Temporary folder security was not read");
        add(owner);
        for (DWORD i = 0; dacl && i < dacl->AceCount; ++i) {
            ACE_HEADER* header = nullptr;
            if (GetAce(dacl, i, reinterpret_cast<void**>(&header)) && header->AceType == ACCESS_ALLOWED_ACE_TYPE)
                add(&reinterpret_cast<ACCESS_ALLOWED_ACE*>(header)->SidStart);
        }
        LocalFree(descriptor);
    }
    for (const auto& sid : result) if (sid == L"S-1-5-32-546") throw std::runtime_error("Guests may change the temporary folder; the tests need it untrusted");
    return result;
}

// Tests run against a private HKCU key and temporary directories; the real HKLM IFEO key is never opened.
struct Sandbox {
    std::wstring path = L"Software\\NKS Task Manager Tests\\IFEO-" + std::to_wstring(GetCurrentProcessId());
    std::filesystem::path root = std::filesystem::temp_directory_path() / (L"nks-ifeo-" + std::to_wstring(GetCurrentProcessId()));
    HKEY key{};
    Sandbox()
    {
        RegDeleteTreeW(HKEY_CURRENT_USER, path.c_str());
        std::error_code ignored; std::filesystem::remove_all(root, ignored);
        if (RegCreateKeyExW(HKEY_CURRENT_USER, path.c_str(), 0, nullptr, REG_OPTION_VOLATILE, KEY_ALL_ACCESS, nullptr, &key, nullptr))
            throw std::runtime_error("Temporary registry key was not created");
        create(root.wstring(), protectedAcl, true);
    }
    ~Sandbox()
    {
        RegCloseKey(key); RegDeleteTreeW(HKEY_CURRENT_USER, path.c_str());
        RegDeleteKeyW(HKEY_CURRENT_USER, L"Software\\NKS Task Manager Tests"); // fails harmlessly while another run still uses it
        std::error_code ignored; std::filesystem::remove_all(root, ignored);
    }
    std::wstring at(const wchar_t* relative) const { return (root / relative).wstring(); }
};

std::optional<std::wstring> value(HKEY key, const wchar_t* name, DWORD* type = nullptr)
{
    wchar_t buffer[1024]{}; DWORD size = sizeof(buffer) - sizeof(wchar_t), kind = 0;
    if (RegQueryValueExW(key, name, nullptr, &kind, reinterpret_cast<BYTE*>(buffer), &size)) return std::nullopt;
    if (type) *type = kind;
    return std::wstring(buffer);
}
void set(HKEY key, const wchar_t* name, const std::wstring& text, DWORD type = REG_SZ)
{
    if (RegSetValueExW(key, name, 0, type, reinterpret_cast<const BYTE*>(text.c_str()), static_cast<DWORD>((text.size() + 1) * sizeof(wchar_t))))
        throw std::runtime_error("Test value was not written");
}
bool ok(const Result& result, Status before, bool changed, bool dropped = false)
{
    return !result.error && result.before == before && result.changed == changed && result.backupDropped == dropped;
}

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

void locations()
{
    Sandbox box; const auto root = box.root.wstring();
    create(box.at(L"NKS Task Manager"), protectedAcl, true);
    const auto installed = box.at(L"NKS Task Manager\\NksTaskMgr.WinUI.exe");
    create(installed, protectedAcl, false);
    require(machineLocation(installed, root, trustedTree) == Location::safe, "Protected install was refused");
    require(machineLocation(installed, root + L"\\", trustedTree) == Location::safe, "Trailing separator of the root changed the result");
    require(machineLocation(installed, root) == Location::writable, "A user-owned install passed the default administrator-only rule");
    require(machineLocation(installed, box.at(L"NKS Task Manager\\other"), trustedTree) == Location::outside, "Path outside the root was accepted");
    require(machineLocation(box.at(L"NKS Task Manager.\\NksTaskMgr.WinUI.exe"), root, trustedTree) == Location::outside, "Trailing dot was accepted");
    require(machineLocation(box.at(L"NKS Task Manager \\NksTaskMgr.WinUI.exe"), root, trustedTree) == Location::outside, "Trailing space was accepted");
    require(machineLocation(box.at(L"NKS Task Manager\\.\\NksTaskMgr.WinUI.exe"), root, trustedTree) == Location::outside, "Dot component was accepted");
    require(machineLocation(box.at(L"NKS Task Manager\\missing\\NksTaskMgr.WinUI.exe"), root, trustedTree) == Location::unreadable, "Missing file was accepted");

    create(box.at(L"Open"), protectedAcl + L"(A;;0x1301bf;;;BG)", true);
    create(box.at(L"Open\\NksTaskMgr.WinUI.exe"), protectedAcl, false);
    require(machineLocation(box.at(L"Open\\NksTaskMgr.WinUI.exe"), root, trustedTree) == Location::writable, "Writable parent directory was accepted");
    create(box.at(L"AddFile"), protectedAcl + L"(A;;0x2;;;BG)", true);
    create(box.at(L"AddFile\\NksTaskMgr.WinUI.exe"), protectedAcl, false);
    require(machineLocation(box.at(L"AddFile\\NksTaskMgr.WinUI.exe"), root, trustedTree) == Location::writable, "Add-file right on a parent was accepted");
    create(box.at(L"NKS Task Manager\\WritableFile"), protectedAcl, true);
    create(box.at(L"NKS Task Manager\\WritableFile\\NksTaskMgr.WinUI.exe"), protectedAcl + L"(A;;FW;;;WD)", false);
    require(machineLocation(box.at(L"NKS Task Manager\\WritableFile\\NksTaskMgr.WinUI.exe"), root, trustedTree) == Location::writable, "Writable file was accepted");
    create(box.at(L"Dac"), protectedAcl + L"(A;;WD;;;BG)", true);
    create(box.at(L"Dac\\NksTaskMgr.WinUI.exe"), protectedAcl, false);
    require(machineLocation(box.at(L"Dac\\NksTaskMgr.WinUI.exe"), root, trustedTree) == Location::writable, "Change-permissions right was accepted");
    create(box.at(L"Inherit"), protectedAcl + L"(A;OICIIO;FA;;;BG)", true);
    create(box.at(L"Inherit\\NksTaskMgr.WinUI.exe"), protectedAcl, false);
    require(machineLocation(box.at(L"Inherit\\NksTaskMgr.WinUI.exe"), root, trustedTree) == Location::safe, "Inherit-only entry was applied to the directory");
    create(box.at(L"Inherit\\Null"), L"D:NO_ACCESS_CONTROL", true);
    create(box.at(L"Inherit\\Null\\NksTaskMgr.WinUI.exe"), protectedAcl, false);
    require(machineLocation(box.at(L"Inherit\\Null\\NksTaskMgr.WinUI.exe"), root, trustedTree) == Location::writable, "Null DACL was accepted");
}

void lifecycle()
{
    Sandbox box; const HKEY key = box.key; const auto above = trustedAbove(std::filesystem::temp_directory_path()); const Trusted trusted = above;
    create(box.at(L"procexp64.exe"), protectedAcl, false);
    create(box.at(L"tool.exe"), protectedAcl, false);
    const auto foreign = L"\"" + box.at(L"procexp64.exe") + L"\"";
    const auto other = L"\"" + box.at(L"tool.exe") + L"\" /x";

    // Enable with no previous value, then restore to nothing.
    require(ok(enable(key, gui), Status::notRegistered, true), "First enable failed");
    require(value(key, debuggerValue) == quoted && !value(key, backupValue), "Enable without a previous value wrote a backup");
    require(status(key, gui) == Status::registered, "Own registration was not recognized");
    require(ok(disable(key, gui, trusted), Status::registered, true), "Disable failed");
    require(!value(key, debuggerValue) && !value(key, backupValue), "Disable left values behind");

    // Enable over a foreign replacement, twice; the backup keeps the foreign value.
    set(key, debuggerValue, foreign);
    require(status(key, gui) == Status::foreign, "Foreign registration was not recognized");
    require(ok(enable(key, gui), Status::foreign, true), "Enable over a foreign value failed");
    require(value(key, debuggerValue) == quoted && value(key, backupValue) == foreign, "Foreign value was not backed up");
    require(ok(enable(key, gui), Status::registered, false), "Second enable was not idempotent");
    require(value(key, backupValue) == foreign, "Second enable overwrote the backup with its own value");
    const auto preview = plan(key, gui, trusted);
    require(!preview.error && preview.status == Status::registered && preview.debugger == quoted && preview.hasBackup
        && preview.backup == foreign && preview.backupSafe, "Plan did not show the value to restore");
    require(ok(disable(key, gui, trusted), Status::registered, true), "Disable after a foreign backup failed");
    require(value(key, debuggerValue) == foreign && !value(key, backupValue), "Foreign value was not restored");

    // Someone else replaced NKS after it was enabled: disable must not touch anything.
    require(ok(enable(key, gui), Status::foreign, true), "Re-enable failed");
    set(key, debuggerValue, other);
    require(ok(disable(key, gui, trusted), Status::foreign, false), "Disable changed a newer foreign registration");
    require(value(key, debuggerValue) == other && value(key, backupValue) == foreign, "Newer foreign registration was modified");
    // Enabling again backs up the newer foreign value instead of the older one.
    require(ok(enable(key, gui), Status::foreign, true), "Enable over a newer foreign value failed");
    require(value(key, backupValue) == other, "Backup did not follow the newest foreign value");

    // A moved NKS install leaves a stale registration that the new path replaces and can restore.
    set(key, debuggerValue, L"\"D:\\Old place\\NksTaskMgr.WinUI.exe\"");
    require(status(key, gui) == Status::otherNks, "Stale NKS path was not recognized");
    require(ok(enable(key, gui), Status::otherNks, true), "Enable over a stale NKS path failed");
    require(value(key, debuggerValue) == quoted && value(key, backupValue) == other, "Stale NKS path was backed up");
    set(key, debuggerValue, L"D:\\Old\\NKSTASKMGR.WINUI.EXE");
    require(ok(disable(key, gui, trusted), Status::otherNks, true), "Disable from a stale NKS path failed");
    require(value(key, debuggerValue) == other && !value(key, backupValue), "Stale disable did not restore the backup");

    // Own path with arguments counts as registered and is rewritten to the canonical form.
    set(key, debuggerValue, L"\"c:\\program files\\nks task manager\\nkstaskmgr.winui.exe\" /x");
    require(status(key, gui) == Status::registered, "Own path with other case and arguments was not recognized");
    require(ok(enable(key, gui), Status::registered, true) && value(key, debuggerValue) == quoted, "Own value was not normalized");
    require(!value(key, backupValue), "Normalizing own value wrote a backup");
    require(ok(disable(key, gui, trusted), Status::registered, true), "Disable after normalizing failed");

    // A foreign REG_EXPAND_SZ value keeps its type through backup and restore.
    SetEnvironmentVariableW(L"NKS_IFEO_TEST", box.root.c_str());
    set(key, debuggerValue, L"%NKS_IFEO_TEST%\\tool.exe", REG_EXPAND_SZ);
    require(ok(enable(key, gui), Status::foreign, true), "Enable over REG_EXPAND_SZ failed");
    require(ok(disable(key, gui, trusted), Status::registered, true), "Disable over REG_EXPAND_SZ backup failed");
    DWORD type = 0;
    require(value(key, debuggerValue, &type) == L"%NKS_IFEO_TEST%\\tool.exe" && type == REG_EXPAND_SZ, "Value type was not restored");
    // Windows runs an unquoted path with spaces as C:\Program, so it is not NKS.
    set(key, debuggerValue, L"C:\\Program Files\\NKS Task Manager\\NksTaskMgr.WinUI.exe");
    require(status(key, gui) == Status::foreign, "Unquoted path with spaces was treated as NKS");
    require(enable(key, L"relative\\NksTaskMgr.WinUI.exe").error == ERROR_INVALID_PARAMETER, "Relative path was registered");
    require(disable(key, L"relative\\NksTaskMgr.WinUI.exe").error == ERROR_INVALID_PARAMETER, "Relative path was used for disable");
}

void unsafeBackups()
{
    Sandbox box; const HKEY key = box.key; const auto above = trustedAbove(std::filesystem::temp_directory_path()); const Trusted trusted = above;
    create(box.at(L"Shared"), protectedAcl + L"(A;;0x1301bf;;;BG)", true);
    create(box.at(L"Shared\\tool.exe"), protectedAcl, false);
    create(box.at(L"tool.exe"), protectedAcl, false);
    const std::wstring previousValues[] = {
        L"\"" + box.at(L"missing.exe") + L"\"",          // program no longer exists
        L"\"" + box.at(L"Shared\\tool.exe") + L"\"",     // directory writable by non-administrators
        L"\"" + box.at(L"tool.exe.") + L"\"",            // not a canonical path
        L"tool.exe",                                     // relative name resolved through PATH
        box.at(L"Shared") + L" dir\\tool.exe",           // unquoted, Windows would try the shorter path first
    };
    for (const auto& previous : previousValues) {
        set(key, debuggerValue, previous);
        require(ok(enable(key, gui), Status::foreign, true), "Enable over an unsafe previous value failed");
        const auto preview = plan(key, gui, trusted);
        require(!preview.error && preview.hasBackup && !preview.backupSafe, "Plan offered an unsafe value for restore");
        require(ok(disable(key, gui, trusted), Status::registered, true, true), "Unsafe previous value was not dropped");
        require(!value(key, debuggerValue) && !value(key, backupValue), "Unsafe previous value was restored or kept");
    }
    // The default rule trusts only SYSTEM, Administrators and TrustedInstaller, so a user-owned program is not restored.
    set(key, debuggerValue, L"\"" + box.at(L"tool.exe") + L"\"");
    require(ok(enable(key, gui), Status::foreign, true), "Enable over a user-owned value failed");
    require(ok(disable(key, gui), Status::registered, true, true), "User-owned previous program passed the default rule");
}
}

int main()
{
    try {
        paths(); locations(); lifecycle(); unsafeBackups();
        std::cout << "taskmgr replacement: " << cases << " checks passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "taskmgr replacement failed: " << error.what() << "\n";
        return 1;
    }
}
