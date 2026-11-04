#include "startup_items.hpp"
#include <shlobj.h>
#include <shobjidl.h>
#include <wrl/client.h>
#include <algorithm>
#include <utility>

namespace nks {
namespace {

constexpr std::size_t maximumItems = 4096;
constexpr wchar_t approvedPath[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\";

struct RegistrySource {
    StartupSource source;
    bool machine;
    const wchar_t* path;
    const wchar_t* approved;
};
constexpr RegistrySource registrySources[] = {
    {StartupSource::userRun, false, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run", L"Run"},
    {StartupSource::userRunOnce, false, L"Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce", nullptr},
    {StartupSource::machineRun, true, L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", L"Run"},
    {StartupSource::machineRunOnce, true, L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce", nullptr},
    {StartupSource::machineRun32, true, L"SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run", L"Run32"},
    {StartupSource::machineRunOnce32, true, L"SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\RunOnce", nullptr},
};

struct Key {
    HKEY value{};
    ~Key() { if (value) RegCloseKey(value); }
};

using Values = std::vector<std::pair<std::wstring, std::vector<std::uint8_t>>>;

bool same(std::wstring_view left, std::wstring_view right)
{
    return CompareStringOrdinal(left.data(), static_cast<int>(left.size()), right.data(), static_cast<int>(right.size()), TRUE) == CSTR_EQUAL;
}

bool isFile(const std::wstring& path)
{
    const auto attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}

Values readValues(HKEY root, const std::wstring& path, bool text)
{
    Values result;
    Key key;
    if (RegOpenKeyExW(root, path.c_str(), 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &key.value) != ERROR_SUCCESS) return result;
    DWORD count{}, nameMax{}, dataMax{};
    if (RegQueryInfoKeyW(key.value, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, &count, &nameMax, &dataMax, nullptr, nullptr) != ERROR_SUCCESS)
        return result;
    std::wstring name(nameMax + 1, L'\0');
    std::vector<std::uint8_t> data(dataMax);
    for (DWORD i = 0; i < count && result.size() < maximumItems; ++i) {
        DWORD nameLength = nameMax + 1, type{}, size = dataMax;
        // A value changed during enumeration reports ERROR_MORE_DATA and is skipped until the next read.
        if (RegEnumValueW(key.value, i, name.data(), &nameLength, nullptr, &type, data.data(), &size) != ERROR_SUCCESS) continue;
        if (text ? type != REG_SZ && type != REG_EXPAND_SZ : type != REG_BINARY) continue;
        result.emplace_back(std::wstring(name.data(), nameLength), std::vector<std::uint8_t>(data.begin(), data.begin() + size));
    }
    return result;
}

std::wstring text(const std::vector<std::uint8_t>& data)
{
    std::wstring value(data.size() / sizeof(wchar_t), L'\0');
    std::copy(data.begin(), data.begin() + value.size() * sizeof(wchar_t), reinterpret_cast<std::uint8_t*>(value.data()));
    value.resize(std::wcslen(value.c_str()));
    return value;
}

std::wstring expand(const std::wstring& source)
{
    const auto size = ExpandEnvironmentStringsW(source.c_str(), nullptr, 0);
    if (!size || size > 32768) return source;
    std::wstring result(size, L'\0');
    const auto written = ExpandEnvironmentStringsW(source.c_str(), result.data(), size);
    if (!written || written > size) return source;
    result.resize(written - 1);
    return result;
}

void approve(StartupItem& item, const Values& approvals)
{
    const auto found = std::find_if(approvals.begin(), approvals.end(), [&](const auto& value) { return same(value.first, item.name); });
    if (found == approvals.end()) return;
    const auto approval = parseStartupApproved(found->second);
    item.state = approval.disabled ? StartupState::disabled : StartupState::enabled;
    item.changed = approval.changed;
}

void resolveLink(const std::wstring& path, StartupItem& item)
{
    Microsoft::WRL::ComPtr<IShellLinkW> link;
    Microsoft::WRL::ComPtr<IPersistFile> file;
    if (FAILED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link)))
        || FAILED(link.As(&file)) || FAILED(file->Load(path.c_str(), STGM_READ))) return;
    std::wstring target(32768, L'\0'), arguments(32768, L'\0');
    // Resolve() could search the disk or network for a moved target, so only the stored path is read.
    if (link->GetPath(target.data(), static_cast<int>(target.size()), nullptr, 0) != S_OK) return;
    target.resize(std::wcslen(target.c_str()));
    if (FAILED(link->GetArguments(arguments.data(), static_cast<int>(arguments.size())))) arguments.clear();
    arguments.resize(std::wcslen(arguments.c_str()));
    if (target.empty()) return;
    item.target = target;
    item.command = L"\"" + target + L"\"" + (arguments.empty() ? L"" : L" " + arguments);
}

void readFolder(const std::wstring& folder, StartupSource source, HKEY approvalRoot, std::vector<StartupItem>& items)
{
    if (folder.empty()) return;
    WIN32_FIND_DATAW data{};
    const HANDLE find = FindFirstFileExW((folder + L"\\*").c_str(), FindExInfoBasic, &data, FindExSearchNameMatch, nullptr, 0);
    if (find == INVALID_HANDLE_VALUE) return;
    const auto approvals = readValues(approvalRoot, std::wstring(approvedPath) + L"StartupFolder", false);
    do {
        if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || same(data.cFileName, L"desktop.ini")) continue;
        StartupItem item{source, StartupState::enabled, 0, data.cFileName, {}, folder, {}};
        item.command = item.target = folder + L"\\" + item.name;
        if (item.name.size() > 4 && same(std::wstring_view(item.name).substr(item.name.size() - 4), L".lnk")) resolveLink(folder + L"\\" + item.name, item);
        approve(item, approvals);
        items.push_back(std::move(item));
    } while (items.size() < maximumItems && FindNextFileW(find, &data));
    FindClose(find);
}

std::wstring knownFolder(REFKNOWNFOLDERID id)
{
    PWSTR path{};
    std::wstring result;
    if (SUCCEEDED(SHGetKnownFolderPath(id, KF_FLAG_DONT_VERIFY, nullptr, &path)) && path) result = path;
    CoTaskMemFree(path);
    return result;
}

}

StartupLocations defaultStartupLocations()
{
    StartupLocations locations;
    locations.userFolder = knownFolder(FOLDERID_Startup);
    locations.commonFolder = knownFolder(FOLDERID_CommonStartup);
    return locations;
}

StartupApproval parseStartupApproved(std::span<const std::uint8_t> value)
{
    StartupApproval result;
    if (value.empty()) return result;
    result.disabled = (value[0] & 1) != 0;
    if (value.size() >= 12)
        for (int i = 0; i < 8; ++i) result.changed |= static_cast<std::uint64_t>(value[4 + i]) << (8 * i);
    return result;
}

std::array<std::uint8_t, 12> startupApprovedValue(std::span<const std::uint8_t> previous, bool enabled, std::uint64_t now)
{
    std::array<std::uint8_t, 12> value{};
    const std::uint8_t flavor = !previous.empty() && (previous[0] & 0xfe) == 0x06 ? 0x06 : 0x02;
    value[0] = static_cast<std::uint8_t>(flavor | (enabled ? 0 : 1));
    if (!enabled)
        for (int i = 0; i < 8; ++i) value[4 + i] = static_cast<std::uint8_t>(now >> (8 * i));
    return value;
}

std::wstring startupTarget(std::wstring_view command)
{
    const auto expanded = expand(std::wstring(command));
    std::wstring_view rest = expanded;
    while (!rest.empty() && (rest.front() == L' ' || rest.front() == L'\t')) rest.remove_prefix(1);
    std::wstring path;
    if (!rest.empty() && rest.front() == L'"') {
        rest.remove_prefix(1);
        path = rest.substr(0, rest.find(L'"'));
    } else {
        // Unquoted paths with spaces resolve to the shortest existing prefix, as CreateProcess does.
        if (rest.substr(0, rest.find(L' ')).find_first_of(L"\\/") != std::wstring_view::npos) {
            for (auto end = rest.find(L' ');; end = rest.find(L' ', end + 1)) {
                const std::wstring candidate(rest.substr(0, end));
                if (isFile(candidate)) { path = candidate; break; }
                if (isFile(candidate + L".exe")) { path = candidate + L".exe"; break; }
                if (end == std::wstring_view::npos) break;
            }
        }
        if (path.empty()) path = rest.substr(0, rest.find(L' '));
    }
    if (!path.empty() && path.find_first_of(L"\\/") == std::wstring::npos) {
        std::wstring found(32768, L'\0');
        const auto length = SearchPathW(nullptr, path.c_str(), L".exe", static_cast<DWORD>(found.size()), found.data(), nullptr);
        if (length && length < found.size()) path.assign(found.data(), length);
    }
    return path;
}

std::vector<StartupItem> readStartupItems(const StartupLocations& locations)
{
    std::vector<StartupItem> items;
    for (const auto& source : registrySources) {
        const HKEY root = source.machine ? locations.machine : locations.user;
        const auto approvals = source.approved ? readValues(root, std::wstring(approvedPath) + source.approved, false) : Values{};
        const std::wstring location = std::wstring(source.machine ? L"HKLM\\" : L"HKCU\\") + source.path;
        for (const auto& [name, data] : readValues(root, source.path, true)) {
            if (items.size() >= maximumItems) return items;
            StartupItem item{source.source, source.approved ? StartupState::enabled : StartupState::runOnce, 0, name, text(data), location, {}};
            item.target = startupTarget(item.command);
            if (source.approved) approve(item, approvals);
            items.push_back(std::move(item));
        }
    }
    const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    readFolder(locations.userFolder, StartupSource::userFolder, locations.user, items);
    readFolder(locations.commonFolder, StartupSource::commonFolder, locations.machine, items);
    if (SUCCEEDED(com)) CoUninitialize();
    return items;
}

DWORD setStartupItemEnabled(const StartupLocations& locations, StartupSource source, std::wstring_view name, bool enabled)
{
    if (name.empty() || name.size() > 16383 || name.find(L'\0') != std::wstring_view::npos) return ERROR_INVALID_PARAMETER;
    const std::wstring value(name);
    const wchar_t* subkey{};
    if (source == StartupSource::userRun) {
        Key run;
        if (const auto error = RegOpenKeyExW(locations.user, registrySources[0].path, 0, KEY_QUERY_VALUE, &run.value)) return error;
        DWORD type{};
        if (const auto error = RegQueryValueExW(run.value, value.c_str(), nullptr, &type, nullptr, nullptr)) return error;
        if (type != REG_SZ && type != REG_EXPAND_SZ) return ERROR_INVALID_DATA;
        subkey = L"Run";
    } else if (source == StartupSource::userFolder) {
        if (value.find_first_of(L"\\/:") != std::wstring::npos || value == L"." || value == L".."
            || locations.userFolder.empty() || !isFile(locations.userFolder + L"\\" + value)) return ERROR_FILE_NOT_FOUND;
        subkey = L"StartupFolder";
    } else return ERROR_NOT_SUPPORTED;
    Key approved;
    if (const auto error = RegCreateKeyExW(locations.user, (std::wstring(approvedPath) + subkey).c_str(), 0, nullptr, 0,
        KEY_QUERY_VALUE | KEY_SET_VALUE, nullptr, &approved.value, nullptr)) return error;
    std::array<std::uint8_t, 64> previous{};
    DWORD size = static_cast<DWORD>(previous.size()), type{};
    std::span<const std::uint8_t> old;
    if (RegQueryValueExW(approved.value, value.c_str(), nullptr, &type, previous.data(), &size) == ERROR_SUCCESS && type == REG_BINARY)
        old = std::span<const std::uint8_t>(previous.data(), size);
    // Repeating the current state keeps the original FILETIME of disabling.
    if (!old.empty() && parseStartupApproved(old).disabled == !enabled) return ERROR_SUCCESS;
    FILETIME now{};
    GetSystemTimeAsFileTime(&now);
    const auto bytes = startupApprovedValue(old, enabled, (static_cast<std::uint64_t>(now.dwHighDateTime) << 32) | now.dwLowDateTime);
    return RegSetValueExW(approved.value, value.c_str(), 0, REG_BINARY, bytes.data(), static_cast<DWORD>(bytes.size()));
}

}
