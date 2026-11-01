#include "scope.hpp"
#include <aclapi.h>
#include <shlobj.h>
#include <algorithm>

namespace nks::update {
namespace {
constexpr wchar_t uninstallKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{3DC15B35-D778-4DD6-AF81-55EC144B6575}_is1";
std::wstring canonical(HANDLE file)
{
    std::wstring result(32768, L'\0');
    const auto size = GetFinalPathNameByHandleW(file, result.data(), static_cast<DWORD>(result.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_GUID);
    if (!size || size >= result.size()) throw Error(Status::rejected, "Cannot canonicalize application root", GetLastError());
    result.resize(size); while (!result.empty() && result.back() == L'\\') result.pop_back(); return result;
}
broker::Handle directory(const std::wstring& path)
{
    broker::Handle file(CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES | READ_CONTROL, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    FILE_ATTRIBUTE_TAG_INFO attributes{};
    if (!file || file.get() == INVALID_HANDLE_VALUE || !GetFileInformationByHandleEx(file.get(), FileAttributeTagInfo, &attributes, sizeof(attributes))
        || (attributes.FileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) != FILE_ATTRIBUTE_DIRECTORY)
        throw Error(Status::rejected, "Application root is inaccessible or redirected", GetLastError());
    return file;
}
bool below(const std::wstring& path, const std::wstring& parent)
{
    return path.size() > parent.size() && path[parent.size()] == L'\\' && _wcsnicmp(path.c_str(), parent.c_str(), parent.size()) == 0;
}
void excludeKnown(const Scope& scope, REFKNOWNFOLDERID id, bool descendants)
{
    wchar_t* path = nullptr;
    const HRESULT status = SHGetKnownFolderPath(id, KF_FLAG_DONT_VERIFY, nullptr, &path);
    if (FAILED(status)) throw Error(Status::rejected, "Cannot establish a protected-directory boundary");
    std::wstring value(path); CoTaskMemFree(path);
    auto held = directory(value); const auto known = canonical(held.get());
    if (_wcsicmp(scope.path.c_str(), known.c_str()) == 0 || (descendants && below(scope.path, known)))
        throw Error(Status::rejected, "Profile, volume and system directory roots cannot be updated");
}
std::wstring registryText(HKEY key, const wchar_t* name)
{
    DWORD type = 0, size = 0;
    if (RegQueryValueExW(key, name, nullptr, &type, nullptr, &size) != ERROR_SUCCESS || type != REG_SZ || size < 2 || size > 65536 || size % 2) return {};
    std::wstring value(size / 2, L'\0');
    if (RegQueryValueExW(key, name, nullptr, &type, reinterpret_cast<BYTE*>(value.data()), &size) != ERROR_SUCCESS || value.back() != 0) return {};
    value.pop_back(); if (value.find(L'\0') != std::wstring::npos) return {}; return value;
}
bool registered(const Scope& scope)
{
    for (DWORD view : {KEY_WOW64_64KEY, KEY_WOW64_32KEY}) {
        HKEY raw = nullptr;
        if (RegOpenKeyExW(HKEY_CURRENT_USER, uninstallKey, 0, KEY_QUERY_VALUE | view, &raw) != ERROR_SUCCESS) continue;
        struct Key { HKEY value; ~Key() { RegCloseKey(value); } } key{raw};
        const auto location = registryText(raw, L"InstallLocation");
        auto uninstaller = registryText(raw, L"UninstallString");
        if (location.empty() || uninstaller.empty()) continue;
        if (uninstaller.front() == L'"' && uninstaller.back() == L'"') uninstaller = uninstaller.substr(1, uninstaller.size() - 2);
        if (uninstaller.find(L'"') != std::wstring::npos) continue;
        try {
            auto folder = directory(location); FILE_ID_INFO id{};
            if (!GetFileInformationByHandleEx(folder.get(), FileIdInfo, &id, sizeof(id)) || !sameRoot(id, scope.id)) continue;
            broker::Handle file(CreateFileW(uninstaller.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
            FILE_ATTRIBUTE_TAG_INFO attributes{};
            if (!file || file.get() == INVALID_HANDLE_VALUE || !GetFileInformationByHandleEx(file.get(), FileAttributeTagInfo, &attributes, sizeof(attributes))
                || (attributes.FileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))) continue;
            if (_wcsicmp(parentPath(canonical(file.get())).c_str(), scope.path.c_str()) == 0) return true;
        } catch (const Error&) { }
    }
    return false;
}
bool portable(const Scope& scope, const broker::Peer& gui)
{
    constexpr char marker[] = "format=1\nappId=3DC15B35-D778-4DD6-AF81-55EC144B6575\n";
    broker::Handle file(CreateFileW((scope.path + L"\\portable.nks").c_str(), GENERIC_READ | READ_CONTROL, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    FILE_ATTRIBUTE_TAG_INFO attributes{}; LARGE_INTEGER size{};
    if (!file || file.get() == INVALID_HANDLE_VALUE || !GetFileInformationByHandleEx(file.get(), FileAttributeTagInfo, &attributes, sizeof(attributes))
        || (attributes.FileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) || !GetFileSizeEx(file.get(), &size)
        || size.QuadPart != static_cast<LONGLONG>(sizeof(marker) - 1)) return false;
    PSECURITY_DESCRIPTOR descriptor = nullptr; PSID owner = nullptr;
    const DWORD status = GetSecurityInfo(file.get(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr, nullptr, &descriptor);
    const bool owns = !status && owner && EqualSid(owner, const_cast<std::byte*>(gui.token.user.data()));
    if (descriptor) LocalFree(descriptor); if (!owns) return false;
    std::array<char, sizeof(marker)> bytes{}; DWORD count = 0;
    return ReadFile(file.get(), bytes.data(), static_cast<DWORD>(sizeof(marker) - 1), &count, nullptr)
        && count == sizeof(marker) - 1 && std::equal(bytes.begin(), bytes.begin() + count, marker);
}
}

bool sameRoot(const FILE_ID_INFO& left, const FILE_ID_INFO& right) noexcept
{
    bool nonempty = false; for (auto value : left.FileId.Identifier) nonempty = nonempty || value != 0;
    return nonempty && equalId(left, right);
}
Scope classifyScope(const broker::Peer& gui)
{
    Scope scope; scope.path = parentPath(gui.path); scope.root = directory(scope.path);
    if (!GetFileInformationByHandleEx(scope.root.get(), FileIdInfo, &scope.id, sizeof(scope.id))) throw Error(Status::rejected, "Cannot identify application root", GetLastError());
    if (scope.path.find(L'"') != std::wstring::npos || scope.path.find(L'\\', 4) == std::wstring::npos)
        throw Error(Status::rejected, "Application root cannot be a volume root");
    PSECURITY_DESCRIPTOR descriptor = nullptr; PSID owner = nullptr;
    const DWORD status = GetSecurityInfo(scope.root.get(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr, nullptr, &descriptor);
    const bool owned = !status && owner && EqualSid(owner, const_cast<std::byte*>(gui.token.user.data()));
    if (descriptor) LocalFree(descriptor); if (!owned) throw Error(Status::rejected, "Application root is not owned by the current user");
    excludeKnown(scope, FOLDERID_Profile, false); excludeKnown(scope, FOLDERID_LocalAppData, false); excludeKnown(scope, FOLDERID_RoamingAppData, false);
    excludeKnown(scope, FOLDERID_Windows, true); excludeKnown(scope, FOLDERID_ProgramFiles, true); excludeKnown(scope, FOLDERID_ProgramFilesX86, true); excludeKnown(scope, FOLDERID_ProgramData, true);
    const bool installed = registered(scope), marked = portable(scope, gui);
    const bool markerPresent = GetFileAttributesW((scope.path + L"\\portable.nks").c_str()) != INVALID_FILE_ATTRIBUTES
        || (GetLastError() != ERROR_FILE_NOT_FOUND && GetLastError() != ERROR_PATH_NOT_FOUND);
    if (installed && !markerPresent) scope.mode = InstallMode::installed;
    else if (!installed && marked) scope.mode = InstallMode::portable;
    else scope.detail = installed ? L"Portable marker conflicts with the user Inno registration." : L"No verified user Inno registration or portable marker was found.";
    return scope;
}
void requireScope(const Scope& scope)
{
    if (scope.mode == InstallMode::unknown) throw Error(Status::rejected, "Application mode is not proven; update and repair are read-only");
    FILE_ID_INFO actual{};
    if (!GetFileInformationByHandleEx(scope.root.get(), FileIdInfo, &actual, sizeof(actual)) || !sameRoot(scope.id, actual))
        throw Error(Status::rejected, "Application root identity changed");
}
}
