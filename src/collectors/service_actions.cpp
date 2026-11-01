#include "service_actions.hpp"
#include <algorithm>

namespace nks::service_actions {
[[noreturn]] void fail(DWORD error, const char* message) {
    const auto status = error == ERROR_ACCESS_DENIED || error == ERROR_PRIVILEGE_NOT_HELD ? Status::accessDenied
        : error == ERROR_NOT_SUPPORTED || error == ERROR_CALL_NOT_IMPLEMENTED ? Status::unsupported
        : error == ERROR_SERVICE_MARKED_FOR_DELETE || error == ERROR_SERVICE_DOES_NOT_EXIST ? Status::changed : Status::error;
    throw Failure(status, error, message);
}
bool nameEqual(const std::wstring& a, const std::wstring& b) {
    return CompareStringOrdinal(a.data(), static_cast<int>(a.size()), b.data(), static_cast<int>(b.size()), TRUE) == CSTR_EQUAL;
}
namespace {
std::wstring stringAt(const std::vector<BYTE>& buffer, const wchar_t* value) {
    if (!value) return {};
    const auto base = reinterpret_cast<std::uintptr_t>(buffer.data()), at = reinterpret_cast<std::uintptr_t>(value);
    if (at < base || at - base >= buffer.size() || at % alignof(wchar_t)) fail(ERROR_INVALID_DATA, "Invalid service configuration pointer");
    const auto* end = std::find(value, value + (buffer.size() - (at - base)) / 2, L'\0');
    if (end == value + (buffer.size() - (at - base)) / 2) fail(ERROR_INVALID_DATA, "Unterminated service configuration text");
    std::wstring text(value, end);
    if (!validText(text)) fail(ERROR_NO_UNICODE_TRANSLATION, "Invalid service configuration text");
    return text;
}
}
Configuration readConfiguration(SC_HANDLE service) {
    DWORD required = 0;
    QueryServiceConfigW(service, nullptr, 0, &required);
    if (GetLastError() != ERROR_INSUFFICIENT_BUFFER) fail(GetLastError(), "Cannot size service configuration");
    for (unsigned attempt = 0; attempt < 3; ++attempt) {
        if (required < sizeof(QUERY_SERVICE_CONFIGW) || required > 8192) fail(ERROR_INVALID_DATA, "Service configuration size limit");
        std::vector<BYTE> buffer(required);
        auto* native = reinterpret_cast<QUERY_SERVICE_CONFIGW*>(buffer.data());
        if (!QueryServiceConfigW(service, native, static_cast<DWORD>(buffer.size()), &required)) {
            const auto error = GetLastError(); if (error == ERROR_INSUFFICIENT_BUFFER) continue; fail(error, "Cannot read service configuration");
        }
        Configuration value;
        value.type = native->dwServiceType; value.startType = native->dwStartType; value.errorControl = native->dwErrorControl; value.tag = native->dwTagId;
        value.title = stringAt(buffer, native->lpDisplayName); value.command = stringAt(buffer, native->lpBinaryPathName);
        value.account = stringAt(buffer, native->lpServiceStartName); value.group = stringAt(buffer, native->lpLoadOrderGroup);
        auto* dependency = native->lpDependencies;
        if (dependency) for (;;) {
            auto text = stringAt(buffer, dependency); if (text.empty()) break;
            if (value.dependencies.size() == maximumServices) throw Failure(Status::limitExceeded, ERROR_MORE_DATA, "Service dependency limit");
            dependency += text.size() + 1; value.dependencies.push_back(std::move(text));
        }
        SERVICE_DELAYED_AUTO_START_INFO delayed{}; DWORD used{};
        if (!QueryServiceConfig2W(service, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, reinterpret_cast<BYTE*>(&delayed), sizeof(delayed), &used))
            fail(GetLastError(), "Cannot verify delayed service configuration");
        value.delayed = delayed.fDelayedAutostart != FALSE; return value;
    }
    fail(ERROR_RETRY, "Service configuration changed during query");
}
SERVICE_STATUS_PROCESS readStatus(SC_HANDLE service) {
    SERVICE_STATUS_PROCESS status{}; DWORD used{};
    if (!QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, reinterpret_cast<BYTE*>(&status), sizeof(status), &used))
        fail(GetLastError(), "Cannot query service state");
    if (status.dwCurrentState < SERVICE_STOPPED || status.dwCurrentState > SERVICE_PAUSED)
        fail(ERROR_INVALID_DATA, "Invalid service state");
    return status;
}
std::vector<Dependent> activeDependents(SC_HANDLE service) {
    std::vector<BYTE> buffer(64000); DWORD required{}, count{};
    if (!EnumDependentServicesW(service, SERVICE_ACTIVE, reinterpret_cast<ENUM_SERVICE_STATUSW*>(buffer.data()), static_cast<DWORD>(buffer.size()), &required, &count))
        fail(GetLastError(), "Cannot enumerate active service dependents");
    if (count > maximumServices || count > buffer.size() / sizeof(ENUM_SERVICE_STATUSW)) throw Failure(Status::limitExceeded, ERROR_MORE_DATA, "Active service dependent limit");
    const auto* rows = reinterpret_cast<ENUM_SERVICE_STATUSW*>(buffer.data());
    std::vector<Dependent> values; values.reserve(count);
    for (DWORD i = 0; i < count; ++i) {
        auto name = stringAt(buffer, rows[i].lpServiceName);
        if (name.empty()) fail(ERROR_INVALID_DATA, "Empty dependent service name");
        values.push_back({std::move(name), rows[i].ServiceStatus.dwCurrentState});
    }
    return values;
}
void verifyName(const Plan& plan, const Node& node) {
    // SCM can deny access 0; QUERY_STATUS detects delete-pending without mutation.
    // All mutations still use the original node.handle.
    Handle probe(OpenServiceW(plan.manager.get(), node.name.c_str(), SERVICE_QUERY_STATUS));
    if (!probe) fail(GetLastError(), "Service name is no longer available");
}
}
