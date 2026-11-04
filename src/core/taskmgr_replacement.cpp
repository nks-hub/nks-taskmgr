#include "taskmgr_replacement.hpp"
#include <optional>
#include <vector>

namespace nks::replacement {
namespace {
struct Value {
    DWORD type{};
    std::vector<BYTE> data;
};

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
}

std::wstring debuggerCommand(std::wstring_view guiPath)
{
    const bool absolute = guiPath.size() > 3 && ((guiPath[0] | 0x20) >= L'a' && (guiPath[0] | 0x20) <= L'z')
        && guiPath[1] == L':' && guiPath[2] == L'\\';
    for (const wchar_t character : guiPath) if (character < L' ' || character == L'"' || character == L'/') return {};
    if (!absolute || guiPath.find(L"\\..\\") != std::wstring_view::npos || !same(fileName(guiPath), guiImage)) return {};
    return L"\"" + std::wstring(guiPath) + L"\"";
}

bool insideDirectory(std::wstring_view path, std::wstring_view directory)
{
    while (!directory.empty() && directory.back() == L'\\') directory.remove_suffix(1);
    return !directory.empty() && path.size() > directory.size() + 1 && path[directory.size()] == L'\\'
        && same(path.substr(0, directory.size()), directory) && path.find(L"\\..\\") == std::wstring_view::npos;
}

Status status(HKEY key, std::wstring_view guiPath, LSTATUS* error)
{
    std::optional<Value> current;
    const auto failure = read(key, debuggerValue, current);
    if (error) *error = failure;
    return classify(current, guiPath);
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

Result disable(HKEY key, std::wstring_view guiPath)
{
    Result result;
    if (debuggerCommand(guiPath).empty()) { result.error = ERROR_INVALID_PARAMETER; return result; }
    std::optional<Value> current, backup;
    if ((result.error = read(key, debuggerValue, current))) return result;
    result.before = classify(current, guiPath);
    if (result.before == Status::notRegistered || result.before == Status::foreign) return result;
    if ((result.error = read(key, backupValue, backup))) return result;
    if ((result.error = backup ? write(key, debuggerValue, *backup) : remove(key, debuggerValue))) return result;
    result.changed = true; result.error = remove(key, backupValue); return result;
}

Status systemStatus(std::wstring_view guiPath, LSTATUS* error)
{
    HKEY key = nullptr;
    auto failure = RegOpenKeyExW(HKEY_LOCAL_MACHINE, systemKey, 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &key);
    if (failure == ERROR_FILE_NOT_FOUND) { key = nullptr; failure = ERROR_SUCCESS; }
    if (failure) { if (error) *error = failure; return Status::notRegistered; }
    const auto result = status(key, guiPath, error);
    if (key) RegCloseKey(key);
    return result;
}

Result applySystem(bool enabled, std::wstring_view guiPath)
{
    constexpr REGSAM access = KEY_QUERY_VALUE | KEY_SET_VALUE | KEY_WOW64_64KEY;
    HKEY key = nullptr; Result result;
    result.error = enabled ? RegCreateKeyExW(HKEY_LOCAL_MACHINE, systemKey, 0, nullptr, REG_OPTION_NON_VOLATILE, access, nullptr, &key, nullptr)
        : RegOpenKeyExW(HKEY_LOCAL_MACHINE, systemKey, 0, access, &key);
    if (!enabled && result.error == ERROR_FILE_NOT_FOUND) { key = nullptr; result.error = ERROR_SUCCESS; }
    if (result.error) return result;
    result = enabled ? enable(key, guiPath) : disable(key, guiPath);
    if (key) RegCloseKey(key);
    return result;
}
}
