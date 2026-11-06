#include "collectors/startup_items.hpp"
#include "collectors/process_details.hpp"
#include "collectors/process_identity.hpp"
#include <objbase.h>
#include <shobjidl.h>
#include <wrl/client.h>
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>

namespace {
namespace fs = std::filesystem;
using Bytes = std::vector<std::uint8_t>;

void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
bool same(const std::wstring& left, const std::wstring& right) { return _wcsicmp(left.c_str(), right.c_str()) == 0; }

void approvedValues()
{
    const Bytes enabled{2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}, disabled{3, 0, 0, 0, 8, 7, 6, 5, 4, 3, 2, 1};
    require(!nks::parseStartupApproved(enabled).disabled, "0x02 was read as disabled");
    require(nks::parseStartupApproved(disabled).disabled && nks::parseStartupApproved(disabled).changed == 0x0102030405060708,
        "0x03 or its FILETIME was misread");
    require(!nks::parseStartupApproved(Bytes{6, 0, 0, 0}).disabled && nks::parseStartupApproved(Bytes{7, 0, 0, 0}).disabled, "0x06/0x07 misread");
    require(!nks::parseStartupApproved({}).disabled, "A missing value disabled the item");
    const auto off = nks::startupApprovedValue({}, false, 0x0102030405060708);
    require(Bytes(off.begin(), off.end()) == disabled, "Disabling did not write 0x03 and the FILETIME");
    const auto on = nks::startupApprovedValue(disabled, true, 0x0102030405060708);
    require(Bytes(on.begin(), on.end()) == enabled, "Enabling did not write 0x02 with a zero FILETIME");
    require(nks::startupApprovedValue(Bytes{6, 0, 0, 0}, false, 1)[0] == 7 && nks::startupApprovedValue(Bytes{7}, true, 1)[0] == 6,
        "The 0x06 flavor was not preserved");
}

void createLink(const fs::path& link, const fs::path& target, const wchar_t* arguments)
{
    Microsoft::WRL::ComPtr<IShellLinkW> shell;
    Microsoft::WRL::ComPtr<IPersistFile> file;
    require(SUCCEEDED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&shell))), "ShellLink unavailable");
    require(SUCCEEDED(shell->SetPath(target.c_str())) && SUCCEEDED(shell->SetArguments(arguments)), "Shortcut setup failed");
    require(SUCCEEDED(shell.As(&file)) && SUCCEEDED(file->Save(link.c_str(), TRUE)), "Shortcut save failed");
}

void targets(const fs::path& tool)
{
    const auto path = tool.wstring();
    require(nks::startupTarget(L"\"" + path + L"\" --tray") == path, "Quoted command lost its image");
    require(nks::startupTarget(L"  " + path + L" --tray") == path, "Unquoted path with spaces was not resolved");
    require(nks::startupTarget(path.substr(0, path.size() - 4) + L" /x") == path, "Implicit .exe was not resolved");
    require(nks::startupTarget(L"\"%NKS_STARTUP_TEST_DIR%\\app tool.exe\" -a") == path, "Environment variables were not expanded");
    wchar_t system[MAX_PATH]{};
    require(GetSystemDirectoryW(system, MAX_PATH) != 0, "System directory unavailable");
    require(same(nks::startupTarget(L"rundll32.exe shell32.dll,Control_RunDLL"), std::wstring(system) + L"\\rundll32.exe"),
        "A bare image name was not found on the search path");
    require(nks::startupTarget(L"C:\\nks missing\\x.exe /a") == L"C:\\nks", "A missing unquoted path did not fall back to the first token");
}

Bytes rawValue(HKEY root, const wchar_t* key, const wchar_t* name)
{
    Bytes value(64);
    DWORD size = static_cast<DWORD>(value.size());
    if (RegGetValueW(root, key, name, RRF_RT_REG_BINARY, nullptr, value.data(), &size) != ERROR_SUCCESS) return {};
    value.resize(size); return value;
}

const nks::StartupItem& find(const std::vector<nks::StartupItem>& items, const wchar_t* name)
{
    const auto found = std::find_if(items.begin(), items.end(), [&](const auto& item) { return item.name == name; });
    if (found == items.end()) throw std::runtime_error("Expected startup item is missing");
    return *found;
}

void setString(HKEY root, const wchar_t* key, const wchar_t* name, const std::wstring& value, DWORD type = REG_SZ)
{
    require(RegSetKeyValueW(root, key, name, type, value.c_str(), static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t))) == ERROR_SUCCESS,
        "Fixture value was not written");
}

void inventory(const fs::path& temp, const fs::path& tool)
{
    // The fixture lives under a temporary HKCU subkey; the real Run and StartupApproved keys are never opened for writing.
    const std::wstring base = L"Software\\NksTaskMgrTests\\Startup-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
    HKEY user{}, machine{};
    require(RegCreateKeyExW(HKEY_CURRENT_USER, (base + L"\\User").c_str(), 0, nullptr, 0, KEY_ALL_ACCESS, nullptr, &user, nullptr) == ERROR_SUCCESS
        && RegCreateKeyExW(HKEY_CURRENT_USER, (base + L"\\Machine").c_str(), 0, nullptr, 0, KEY_ALL_ACCESS, nullptr, &machine, nullptr) == ERROR_SUCCESS,
        "Temporary registry fixture was not created");
    try {
        constexpr wchar_t run[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        constexpr wchar_t approvedRun[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run";
        constexpr wchar_t approvedFolder[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\StartupFolder";
        constexpr wchar_t approvedRun32[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run32";
        setString(user, run, L"Demo", L"\"" + tool.wstring() + L"\" --tray");
        setString(user, run, L"Expanded", L"%NKS_STARTUP_TEST_DIR%\\app tool.exe", REG_EXPAND_SZ);
        setString(user, L"Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce", L"Once", L"cmd.exe /c exit");
        setString(machine, L"SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run", L"Legacy", L"\"" + tool.wstring() + L"\"");
        const Bytes disabled{3, 0, 0, 0, 8, 7, 6, 5, 4, 3, 2, 1};
        require(RegSetKeyValueW(user, approvedRun, L"Demo", REG_BINARY, disabled.data(), 12) == ERROR_SUCCESS
            && RegSetKeyValueW(machine, approvedRun32, L"Legacy", REG_BINARY, disabled.data(), 12) == ERROR_SUCCESS, "Approval fixture failed");
        const auto userFolder = temp / L"User Startup", commonFolder = temp / L"Common Startup";
        fs::create_directories(userFolder); fs::create_directories(commonFolder);
        createLink(userFolder / L"Tool.lnk", tool, L"-x");
        std::ofstream(userFolder / L"note.txt") << "x";
        std::ofstream(userFolder / L"desktop.ini") << "[.ShellClassInfo]";
        const nks::StartupLocations locations{user, machine, userFolder.wstring(), commonFolder.wstring()};

        auto items = nks::readStartupItems(locations);
        require(items.size() == 6, "Unexpected startup item count");
        const auto& demo = find(items, L"Demo");
        require(demo.source == nks::StartupSource::userRun && demo.state == nks::StartupState::disabled && demo.changed == 0x0102030405060708
            && demo.target == tool.wstring() && demo.location == std::wstring(L"HKCU\\") + run, "Run item or its approval was misread");
        require(find(items, L"Expanded").state == nks::StartupState::enabled && find(items, L"Expanded").target == tool.wstring(), "REG_EXPAND_SZ item misread");
        const auto& once = find(items, L"Once");
        require(once.source == nks::StartupSource::userRunOnce && once.state == nks::StartupState::runOnce
            && once.target.ends_with(L"\\cmd.exe"), "RunOnce item misread");
        require(find(items, L"Legacy").source == nks::StartupSource::machineRun32 && find(items, L"Legacy").state == nks::StartupState::disabled,
            "WOW6432Node item or Run32 approval misread");
        const auto& link = find(items, L"Tool.lnk");
        require(link.source == nks::StartupSource::userFolder && link.target == tool.wstring()
            && link.command == L"\"" + tool.wstring() + L"\" -x" && link.location == userFolder.wstring(), "Shortcut target misread");
        require(find(items, L"note.txt").target == (userFolder / L"note.txt").wstring(), "Plain Startup file misread");

        require(nks::setStartupItemEnabled(locations, nks::StartupSource::userRun, L"demo", true) == ERROR_SUCCESS, "Enabling failed");
        require(rawValue(user, approvedRun, L"Demo") == Bytes{2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}, "Enabling wrote an unexpected value");
        require(nks::setStartupItemEnabled(locations, nks::StartupSource::userRun, L"Demo", false) == ERROR_SUCCESS, "Disabling failed");
        const auto first = rawValue(user, approvedRun, L"Demo");
        require(first.size() == 12 && first[0] == 3 && nks::parseStartupApproved(first).changed != 0, "Disabling wrote no FILETIME");
        require(nks::setStartupItemEnabled(locations, nks::StartupSource::userRun, L"Demo", false) == ERROR_SUCCESS
            && rawValue(user, approvedRun, L"Demo") == first, "Repeated disabling replaced the original FILETIME");
        require(nks::setStartupItemEnabled(locations, nks::StartupSource::userFolder, L"Tool.lnk", false) == ERROR_SUCCESS
            && nks::parseStartupApproved(rawValue(user, approvedFolder, L"Tool.lnk")).disabled, "Startup folder item was not disabled");
        items = nks::readStartupItems(locations);
        require(find(items, L"Tool.lnk").state == nks::StartupState::disabled, "Disabled shortcut read back as enabled");
        require(nks::setStartupItemEnabled(locations, nks::StartupSource::machineRun32, L"Legacy", true) == ERROR_SUCCESS
            && rawValue(machine, approvedRun32, L"Legacy") == Bytes{2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0}
            && rawValue(user, approvedRun32, L"Legacy").empty(), "A machine-wide item was not enabled in the machine key");
        require(nks::setStartupItemEnabled(locations, nks::StartupSource::machineRunOnce32, L"Legacy", false) == ERROR_NOT_SUPPORTED,
            "A machine RunOnce item was toggled");
        require(nks::setStartupItemEnabled(locations, nks::StartupSource::userRunOnce, L"Once", false) == ERROR_NOT_SUPPORTED, "RunOnce was toggled");
        require(nks::setStartupItemEnabled(locations, nks::StartupSource::userRun, L"Missing", false) == ERROR_FILE_NOT_FOUND
            && rawValue(user, approvedRun, L"Missing").empty(), "A missing Run value received an approval");
        require(nks::setStartupItemEnabled(locations, nks::StartupSource::userFolder, L"..\\note.txt", false) == ERROR_FILE_NOT_FOUND,
            "A path outside the Startup folder was accepted");
        require(fs::exists(userFolder / L"Tool.lnk") && fs::exists(userFolder / L"note.txt"), "Toggling removed a file");
    } catch (...) {
        RegCloseKey(user); RegCloseKey(machine); RegDeleteTreeW(HKEY_CURRENT_USER, base.c_str()); RegDeleteKeyW(HKEY_CURRENT_USER, L"Software\\NksTaskMgrTests");
        throw;
    }
    RegCloseKey(user); RegCloseKey(machine);
    require(RegDeleteTreeW(HKEY_CURRENT_USER, base.c_str()) == ERROR_SUCCESS, "Temporary registry fixture was not removed");
    RegDeleteKeyW(HKEY_CURRENT_USER, L"Software\\NksTaskMgrTests");
}

void fileSignature()
{
    wchar_t system[MAX_PATH]{};
    require(GetSystemDirectoryW(system, MAX_PATH) != 0, "System directory unavailable");
    nks::ProcessDetailsRequest request;
    request.command = nks::details::Command::fileSignature;
    request.target = {{1, 2}, 0, nks::GenerationKind::sequenceNumber, 0};
    request.modulePath = std::wstring(system) + L"\\rundll32.exe";
    const auto result = nks::readProcessDetails(request);
    require(result.state == nks::details::ResultState::ok && result.signature && result.signature->state == nks::details::SignatureState::valid
        && !result.signature->publisher.empty() && result.flags == nks::details::diskFile, "A Windows image did not verify by path");
    request.target.pid = 4;
    require(nks::readProcessDetails(request).state == nks::details::ResultState::invalidRequest, "A process target was accepted for a file signature");
}

// A real 32-bit (WOW64) process must list its 32-bit modules; a TH32CS_SNAPMODULE32-only snapshot listed none (error 18).
void wow64Modules()
{
    wchar_t wow[MAX_PATH]{};
    if (GetSystemWow64DirectoryW(wow, MAX_PATH) == 0) { std::cout << "WOW64 is not installed; module test skipped\n"; return; }
    std::wstring command = L"\"" + std::wstring(wow) + L"\\cmd.exe\" /c ping -n 30 127.0.0.1";
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION process{};
    require(CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process) != 0, "32-bit cmd.exe did not start");
    nks::DetailHandle child(process.hProcess), thread(process.hThread);
    struct Stop { HANDLE handle; ~Stop() { TerminateProcess(handle, 0); WaitForSingleObject(handle, 5000); } } stop{process.hProcess};
    Sleep(1500);
    const auto identity = nks::queryProcessIdentity(process.hProcess, nks::GenerationKind::creationTime);
    require(identity.state == nks::SampleState::valid, "No identity for the 32-bit process");
    nks::ProcessDetailsRequest request; request.command = nks::details::Command::modules; request.target = identity.identity;
    const auto result = nks::readProcessDetails(request);
    require(result.state == nks::details::ResultState::ok && result.architecture == nks::details::Architecture::x86,
        ("WOW64 modules were not read, error " + std::to_string(result.win32Error)).c_str());
    const std::wstring kernel = std::wstring(wow) + L"\\kernel32.dll";
    require(std::any_of(result.modules.begin(), result.modules.end(), [&](const nks::ProcessModuleDetail& module) {
        return _wcsicmp(module.path.c_str(), kernel.c_str()) == 0 && module.base + module.imageSize <= (std::uint64_t{1} << 32); }),
        "The 32-bit kernel32.dll is missing from the WOW64 module list");
    std::cout << "WOW64 modules passed: " << result.modules.size() << " modules\n";
}
}

int main()
{
    // Shortcuts resolve to long names; CI runners set TEMP to an 8.3 short path (RUNNER~1).
    const auto temp = fs::canonical(fs::temp_directory_path()) / (L"nks-startup-tests-" + std::to_wstring(GetCurrentProcessId()));
    int status = 0;
    const HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    try {
        const auto tool = temp / L"Demo Dir" / L"app tool.exe";
        fs::create_directories(tool.parent_path());
        std::ofstream(tool) << "MZ";
        require(SetEnvironmentVariableW(L"NKS_STARTUP_TEST_DIR", tool.parent_path().c_str()) != 0, "Environment fixture failed");
        approvedValues();
        targets(tool);
        inventory(temp, tool);
        fileSignature();
        wow64Modules();
        std::cout << "Startup inventory tests passed\n";
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        status = 1;
    }
    std::error_code ignored;
    fs::remove_all(temp, ignored);
    if (SUCCEEDED(com)) CoUninitialize();
    return status;
}
