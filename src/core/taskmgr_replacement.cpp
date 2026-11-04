#include "taskmgr_replacement.hpp"
#include <aclapi.h>
#include <sddl.h>
#include <algorithm>
#include <optional>
#include <vector>

namespace nks::replacement {
namespace {
struct Value {
    DWORD type{};
    std::vector<BYTE> data;
};
const std::wstring defaultTrusted[] = {L"S-1-5-18", L"S-1-5-32-544", L"S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"};

LSTATUS read(HKEY key, const wchar_t* name, std::optional<Value>& value)
{
    value.reset();
    if (!key) return ERROR_SUCCESS;
    for (;;) {
        DWORD type = 0, size = 0;
        auto error = RegQueryValueExW(key, name, nullptr, &type, nullptr, &size);
        if (error == ERROR_FILE_NOT_FOUND) return ERROR_SUCCESS;
        if (error) return error;
        Value result{type, std::vector<BYTE>(size)};
        error = RegQueryValueExW(key, name, nullptr, &result.type, result.data.data(), &size);
        if (error == ERROR_MORE_DATA) continue;
        if (error == ERROR_FILE_NOT_FOUND) return ERROR_SUCCESS;
        if (error) return error;
        result.data.resize(size); value = std::move(result); return ERROR_SUCCESS;
    }
}
LSTATUS write(HKEY key, const wchar_t* name, const Value& value)
{
    return RegSetValueExW(key, name, 0, value.type, value.data.data(), static_cast<DWORD>(value.data.size()));
}
LSTATUS remove(HKEY key, const wchar_t* name)
{
    const auto error = RegDeleteValueW(key, name);
    return error == ERROR_FILE_NOT_FOUND ? ERROR_SUCCESS : error;
}
std::wstring text(const Value& value)
{
    std::wstring result(reinterpret_cast<const wchar_t*>(value.data.data()), value.data.size() / sizeof(wchar_t));
    while (!result.empty() && result.back() == L'\0') result.pop_back();
    return result;
}
bool same(std::wstring_view left, std::wstring_view right)
{
    if (left.size() != right.size()) return false;
    return left.empty() || CompareStringOrdinal(left.data(), static_cast<int>(left.size()), right.data(), static_cast<int>(right.size()), TRUE) == CSTR_EQUAL;
}
bool absolute(std::wstring_view path)
{
    return path.size() > 3 && (path[0] | 0x20) >= L'a' && (path[0] | 0x20) <= L'z' && path[1] == L':' && path[2] == L'\\';
}
std::wstring_view fileName(std::wstring_view path)
{
    const auto at = path.find_last_of(L"\\/");
    return at == std::wstring_view::npos ? path : path.substr(at + 1);
}
std::wstring_view executable(std::wstring_view command)
{
    const auto start = command.find_first_not_of(L" \t");
    if (start == std::wstring_view::npos) return {};
    command.remove_prefix(start);
    if (command.front() == L'"') { command.remove_prefix(1); return command.substr(0, command.find(L'"')); }
    return command.substr(0, command.find_first_of(L" \t"));
}
Status classify(const std::optional<Value>& value, std::wstring_view guiPath)
{
    if (!value) return Status::notRegistered;
    if (value->type != REG_SZ && value->type != REG_EXPAND_SZ) return Status::foreign;
    const auto command = text(*value); const auto image = executable(command);
    if (same(image, guiPath)) return Status::registered;
    return same(fileName(image), guiImage) ? Status::otherNks : Status::foreign;
}
/// Win32 drops trailing dots and spaces and resolves "." and "..", so a canonical path has none and survives GetFullPathNameW unchanged.
bool canonical(std::wstring_view path)
{
    for (std::size_t start = 3; start <= path.size();) {
        const auto end = std::min(path.find(L'\\', start), path.size());
        if (end == start || path[end - 1] == L'.' || path[end - 1] == L' ') return false;
        start = end + 1;
    }
    const std::wstring input(path); std::wstring full(32768, L'\0');
    const DWORD size = GetFullPathNameW(input.c_str(), static_cast<DWORD>(full.size()), full.data(), nullptr);
    return size && size < full.size() && std::wstring_view(full.data(), size) == path;
}
class Sids {
public:
    explicit Sids(Trusted trusted)
    {
        for (const auto& text : trusted.empty() ? Trusted(defaultTrusted) : trusted) {
            PSID sid = nullptr;
            if (!ConvertStringSidToSidW(text.c_str(), &sid)) continue;
            const auto bytes = static_cast<const BYTE*>(sid);
            sids_.emplace_back(bytes, bytes + GetLengthSid(sid)); LocalFree(sid);
        }
    }
    bool contains(PSID sid) const
    {
        if (!sid || !IsValidSid(sid)) return false;
        for (const auto& known : sids_) if (EqualSid(const_cast<BYTE*>(known.data()), sid)) return true;
        return false;
    }
private:
    std::vector<std::vector<BYTE>> sids_;
};
/// The owner and every effective allow entry that can write, delete or re-permission the object must be trusted.
Location secure(const std::wstring& path, const Sids& trusted)
{
    constexpr ACCESS_MASK changes = FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_EA | FILE_WRITE_ATTRIBUTES | FILE_DELETE_CHILD
        | DELETE | WRITE_DAC | WRITE_OWNER | GENERIC_WRITE | GENERIC_ALL;
    PSID owner = nullptr; PACL dacl = nullptr; PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (GetNamedSecurityInfoW(path.c_str(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
        &owner, nullptr, &dacl, nullptr, &descriptor)) return Location::unreadable;
    bool safe = trusted.contains(owner) && dacl;
    for (DWORD i = 0; safe && i < dacl->AceCount; ++i) {
        ACE_HEADER* header = nullptr;
        if (!GetAce(dacl, i, reinterpret_cast<void**>(&header))) { safe = false; break; }
        if (header->AceFlags & INHERIT_ONLY_ACE) continue;
        switch (header->AceType) {
        case ACCESS_ALLOWED_ACE_TYPE: case ACCESS_ALLOWED_CALLBACK_ACE_TYPE: {
            const auto ace = reinterpret_cast<ACCESS_ALLOWED_ACE*>(header);
            if ((ace->Mask & changes) && !trusted.contains(&ace->SidStart)) safe = false;
            break;
        }
        case ACCESS_ALLOWED_COMPOUND_ACE_TYPE: case ACCESS_ALLOWED_OBJECT_ACE_TYPE: case ACCESS_ALLOWED_CALLBACK_OBJECT_ACE_TYPE:
            safe = false; break;
        default: break;
        }
    }
    LocalFree(descriptor);
    return safe ? Location::safe : Location::writable;
}
/// The program a backed-up Debugger command starts must exist and, up to its volume root, be protected.
bool safeProgram(const Value& value, Trusted trusted)
{
    if (value.type != REG_SZ && value.type != REG_EXPAND_SZ) return false;
    auto command = text(value);
    if (value.type == REG_EXPAND_SZ) {
        std::wstring expanded(32768, L'\0');
        const DWORD size = ExpandEnvironmentStringsW(command.c_str(), expanded.data(), static_cast<DWORD>(expanded.size()));
        if (!size || size > expanded.size()) return false;
        expanded.resize(size - 1); command = std::move(expanded);
    }
    std::wstring current(executable(command));
    const DWORD attributes = absolute(current) && canonical(current) ? GetFileAttributesW(current.c_str()) : INVALID_FILE_ATTRIBUTES;
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY)) return false;
    const Sids sids(trusted);
    for (;;) {
        if (secure(current, sids) != Location::safe) return false;
        const auto at = current.rfind(L'\\');
        if (at <= 2) return true;
        current.resize(at);
    }
}
LSTATUS openSystem(REGSAM access, bool create, HKEY& key)
{
    key = nullptr; access |= KEY_WOW64_64KEY;
    const auto error = create ? RegCreateKeyExW(HKEY_LOCAL_MACHINE, systemKey, 0, nullptr, REG_OPTION_NON_VOLATILE, access, nullptr, &key, nullptr)
        : RegOpenKeyExW(HKEY_LOCAL_MACHINE, systemKey, 0, access, &key);
    if (error == ERROR_FILE_NOT_FOUND && !create) { key = nullptr; return ERROR_SUCCESS; }
    return error;
}
}

std::wstring debuggerCommand(std::wstring_view guiPath)
{
    for (const wchar_t character : guiPath) if (character < L' ' || character == L'"' || character == L'/') return {};
    if (!absolute(guiPath) || guiPath.find(L"\\..\\") != std::wstring_view::npos || !same(fileName(guiPath), guiImage)) return {};
    return L"\"" + std::wstring(guiPath) + L"\"";
}

bool insideDirectory(std::wstring_view path, std::wstring_view directory)
{
    while (!directory.empty() && directory.back() == L'\\') directory.remove_suffix(1);
    return directory.size() > 2 && path.size() > directory.size() + 1 && path[directory.size()] == L'\\'
        && same(path.substr(0, directory.size()), directory) && path.find(L"\\..\\") == std::wstring_view::npos;
}

Location machineLocation(std::wstring_view path, std::wstring_view directory, Trusted trusted)
{
    if (!canonical(path) || !insideDirectory(path, directory)) return Location::outside;
    while (directory.back() == L'\\') directory.remove_suffix(1);
    const Sids sids(trusted); std::wstring current(path);
    for (;;) {
        if (const auto location = secure(current, sids); location != Location::safe) return location;
        if (current.size() <= directory.size()) return Location::safe;
        current.resize(current.rfind(L'\\'));
    }
}

Status status(HKEY key, std::wstring_view guiPath, LSTATUS* error)
{
    std::optional<Value> current;
    const auto failure = read(key, debuggerValue, current);
    if (error) *error = failure;
    return classify(current, guiPath);
}

Plan plan(HKEY key, std::wstring_view guiPath, Trusted trusted)
{
    Plan result; std::optional<Value> current, backup;
    if ((result.error = read(key, debuggerValue, current)) || (result.error = read(key, backupValue, backup))) return result;
    result.status = classify(current, guiPath);
    if (current) result.debugger = text(*current);
    if ((result.hasBackup = backup.has_value())) { result.backup = text(*backup); result.backupSafe = safeProgram(*backup, trusted); }
    return result;
}

Result enable(HKEY key, std::wstring_view guiPath)
{
    Result result; const auto command = debuggerCommand(guiPath);
    if (!key || command.empty()) { result.error = ERROR_INVALID_PARAMETER; return result; }
    std::optional<Value> current;
    if ((result.error = read(key, debuggerValue, current))) return result;
    result.before = classify(current, guiPath);
    if (result.before == Status::registered && current->type == REG_SZ && text(*current) == command) return result;
    // The backup is written before Debugger so an interruption never loses the foreign value.
    if (result.before == Status::foreign) result.error = write(key, backupValue, *current);
    else if (result.before == Status::notRegistered) result.error = remove(key, backupValue);
    if (result.error) return result;
    const auto bytes = reinterpret_cast<const BYTE*>(command.c_str());
    result.error = write(key, debuggerValue, {REG_SZ, {bytes, bytes + (command.size() + 1) * sizeof(wchar_t)}});
    result.changed = !result.error; return result;
}

Result disable(HKEY key, std::wstring_view guiPath, Trusted trusted)
{
    Result result;
    if (debuggerCommand(guiPath).empty()) { result.error = ERROR_INVALID_PARAMETER; return result; }
    std::optional<Value> current, backup;
    if ((result.error = read(key, debuggerValue, current))) return result;
    result.before = classify(current, guiPath);
    if (result.before == Status::notRegistered || result.before == Status::foreign) return result;
    if ((result.error = read(key, backupValue, backup))) return result;
    // Restoring a program that is gone or replaceable by standard users would hand them every elevated Task Manager start.
    result.backupDropped = backup && !safeProgram(*backup, trusted);
    if ((result.error = backup && !result.backupDropped ? write(key, debuggerValue, *backup) : remove(key, debuggerValue))) return result;
    result.changed = true; result.error = remove(key, backupValue); return result;
}

Status systemStatus(std::wstring_view guiPath, LSTATUS* error)
{
    HKEY key = nullptr;
    if (const auto failure = openSystem(KEY_QUERY_VALUE, false, key)) { if (error) *error = failure; return Status::notRegistered; }
    const auto result = status(key, guiPath, error);
    if (key) RegCloseKey(key);
    return result;
}

Plan systemPlan(std::wstring_view guiPath)
{
    HKEY key = nullptr; Plan result;
    if ((result.error = openSystem(KEY_QUERY_VALUE, false, key))) return result;
    result = plan(key, guiPath);
    if (key) RegCloseKey(key);
    return result;
}

Result applySystem(bool enabled, std::wstring_view guiPath)
{
    HKEY key = nullptr; Result result;
    if ((result.error = openSystem(KEY_QUERY_VALUE | KEY_SET_VALUE, enabled, key))) return result;
    result = enabled ? enable(key, guiPath) : disable(key, guiPath);
    if (key) RegCloseKey(key);
    return result;
}
}
