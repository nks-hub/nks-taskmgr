#include "service_collector.hpp"
#include <algorithm>
#include <set>
#include <stdexcept>
#include <system_error>

namespace nks {
namespace {
constexpr DWORD bufferLimit = 256 * 1024;
constexpr DWORD configLimit = 8192;
struct ServiceHandle {
    SC_HANDLE value{};
    ~ServiceHandle() { if (value) CloseServiceHandle(value); }
    ServiceHandle(const ServiceHandle&) = delete;
    ServiceHandle& operator=(const ServiceHandle&) = delete;
    explicit ServiceHandle(SC_HANDLE handle) : value(handle) { }
};

void fail(const char* operation) { throw std::system_error(static_cast<int>(GetLastError()), std::system_category(), operation); }

std::wstring stringAt(const std::vector<BYTE>& buffer, const wchar_t* pointer)
{
    if (!pointer) return {};
    const auto address = reinterpret_cast<std::uintptr_t>(pointer), base = reinterpret_cast<std::uintptr_t>(buffer.data());
    if (address < base || address - base >= buffer.size() || (address - base) % alignof(wchar_t))
        throw std::runtime_error("Invalid SCM string pointer");
    const auto capacity = (buffer.size() - (address - base)) / sizeof(wchar_t);
    const auto end = std::find(pointer, pointer + capacity, L'\0');
    if (end == pointer + capacity) throw std::runtime_error("Unterminated SCM string");
    return {pointer, end};
}

ServiceConfig configFor(SC_HANDLE manager, const std::wstring& name)
{
    ServiceConfig result;
    ServiceHandle service(OpenServiceW(manager, name.c_str(), SERVICE_QUERY_CONFIG));
    if (!service.value) { result.error = GetLastError(); return result; }
    DWORD needed = 0;
    QueryServiceConfigW(service.value, nullptr, 0, &needed);
    if (GetLastError() != ERROR_INSUFFICIENT_BUFFER) { result.error = GetLastError(); return result; }
    for (unsigned attempt = 0; attempt < 3; ++attempt) {
        if (needed < sizeof(QUERY_SERVICE_CONFIGW) || needed > configLimit) { result.error = ERROR_INVALID_DATA; return result; }
        std::vector<BYTE> buffer(needed);
        auto* config = reinterpret_cast<QUERY_SERVICE_CONFIGW*>(buffer.data());
        if (!QueryServiceConfigW(service.value, config, static_cast<DWORD>(buffer.size()), &needed)) {
            result.error = GetLastError();
            if (result.error == ERROR_INSUFFICIENT_BUFFER) continue;
            return result;
        }
        result.error = 0;
        result.startType = config->dwStartType;
        result.account = stringAt(buffer, config->lpServiceStartName);
        result.group = stringAt(buffer, config->lpLoadOrderGroup);
        result.command = stringAt(buffer, config->lpBinaryPathName);
        const auto* dependency = config->lpDependencies;
        if (dependency) for (;;) {
            auto namePart = stringAt(buffer, dependency);
            if (namePart.empty()) break;
            dependency += namePart.size() + 1;
            result.dependencies.push_back(std::move(namePart));
        }
        if (result.startType == SERVICE_AUTO_START) {
            SERVICE_DELAYED_AUTO_START_INFO delayed{};
            if (QueryServiceConfig2W(service.value, SERVICE_CONFIG_DELAYED_AUTO_START_INFO,
                reinterpret_cast<BYTE*>(&delayed), sizeof(delayed), &needed)) result.delayed = delayed.fDelayedAutostart != FALSE;
            else result.delayedError = GetLastError();
        }
        return result;
    }
    return result;
}
}

ServiceSnapshot ServiceCollector::collect(std::stop_token stop)
{
    const auto begin = std::chrono::steady_clock::now();
    ServiceHandle manager(OpenSCManagerW(nullptr, nullptr, SC_MANAGER_ENUMERATE_SERVICE | SC_MANAGER_CONNECT));
    if (!manager.value) fail("OpenSCManagerW");
    ServiceSnapshot result;
    std::vector<BYTE> buffer(bufferLimit);
    DWORD resume = 0;
    std::set<std::wstring> seen;
    bool complete = false;
    for (unsigned page = 0; page < 16 && !stop.stop_requested(); ++page) {
        DWORD needed = 0, count = 0;
        const auto previous = resume;
        const auto success = EnumServicesStatusExW(manager.value, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
            buffer.data(), static_cast<DWORD>(buffer.size()), &needed, &count, &resume, nullptr);
        const auto error = success ? ERROR_SUCCESS : GetLastError();
        if (!success && error != ERROR_MORE_DATA) { SetLastError(error); fail("EnumServicesStatusExW"); }
        if (count > buffer.size() / sizeof(ENUM_SERVICE_STATUS_PROCESSW)) throw std::runtime_error("Invalid SCM service count");
        const auto* entries = reinterpret_cast<const ENUM_SERVICE_STATUS_PROCESSW*>(buffer.data());
        for (DWORD i = 0; i < count && !stop.stop_requested(); ++i) {
            ServiceRecord record;
            record.name = stringAt(buffer, entries[i].lpServiceName);
            if (!seen.insert(record.name).second) continue;
            if (seen.size() > 16384) throw std::runtime_error("SCM service count limit exceeded");
            record.title = stringAt(buffer, entries[i].lpDisplayName);
            const auto& status = entries[i].ServiceStatusProcess;
            record.state = status.dwCurrentState; record.pid = status.dwProcessId;
            record.type = status.dwServiceType; record.acceptedControls = status.dwControlsAccepted;
            auto found = configs.find(record.name);
            if (found == configs.end() || begin >= found->second.expires) {
                auto config = configFor(manager.value, record.name);
                found = configs.insert_or_assign(record.name, CachedConfig{std::move(config), begin + std::chrono::seconds(15)}).first;
            }
            record.config = found->second.config;
            result.services.push_back(std::move(record));
        }
        if (success) { complete = true; break; }
        if (resume == previous) throw std::runtime_error("SCM enumeration did not advance");
    }
    if (!complete) throw std::runtime_error(stop.stop_requested() ? "SCM collection cancelled" : "SCM enumeration page limit exceeded");
    std::erase_if(configs, [&](const auto& entry) { return !seen.contains(entry.first); });
    result.measuredAt = std::chrono::steady_clock::now();
    result.collectionTime = result.measuredAt - begin;
    return result;
}

ServiceDetail ServiceCollector::detail(const std::wstring& name)
{
    if (name.empty() || name.size() > 256 || name.find(L'\0') != std::wstring::npos)
        throw std::invalid_argument("Invalid service name");
    ServiceDetail result; result.measuredAt = std::chrono::steady_clock::now(); result.name = name;
    ServiceHandle manager(OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT));
    if (!manager.value) fail("OpenSCManagerW");
    result.config = configFor(manager.value, name);
    ServiceHandle statusService(OpenServiceW(manager.value, name.c_str(), SERVICE_QUERY_STATUS));
    SERVICE_STATUS_PROCESS status{}; DWORD needed = 0;
    if (!statusService.value || !QueryServiceStatusEx(statusService.value, SC_STATUS_PROCESS_INFO,
        reinterpret_cast<BYTE*>(&status), sizeof(status), &needed)) result.statusError = GetLastError();
    else {
        result.state = status.dwCurrentState; result.pid = status.dwProcessId;
        if (status.dwCurrentState == SERVICE_RUNNING && status.dwProcessId) {
            const auto process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, status.dwProcessId);
            if (process) {
                FILETIME created{}, exited{}, kernel{}, user{};
                if (GetProcessTimes(process, &created, &exited, &kernel, &user))
                    result.hostCreated100ns = (static_cast<std::uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
                else result.hostError = GetLastError();
                CloseHandle(process);
                SERVICE_STATUS_PROCESS after{};
                if (!QueryServiceStatusEx(statusService.value, SC_STATUS_PROCESS_INFO, reinterpret_cast<BYTE*>(&after), sizeof(after), &needed)
                    || after.dwProcessId != status.dwProcessId || after.dwCurrentState != SERVICE_RUNNING) {
                    result.hostCreated100ns = 0; result.hostError = ERROR_RETRY;
                }
            } else result.hostError = GetLastError();
        }
    }
    ServiceHandle configService(OpenServiceW(manager.value, name.c_str(), SERVICE_QUERY_CONFIG));
    if (!configService.value) result.descriptionError = GetLastError();
    else {
        std::vector<BYTE> buffer(configLimit);
        if (QueryServiceConfig2W(configService.value, SERVICE_CONFIG_DESCRIPTION, buffer.data(), static_cast<DWORD>(buffer.size()), &needed))
            result.description = stringAt(buffer, reinterpret_cast<SERVICE_DESCRIPTIONW*>(buffer.data())->lpDescription);
        else result.descriptionError = GetLastError();
    }
    ServiceHandle dependentService(OpenServiceW(manager.value, name.c_str(), SERVICE_ENUMERATE_DEPENDENTS));
    if (!dependentService.value) result.dependentsError = GetLastError();
    else {
        std::vector<BYTE> buffer(64000); DWORD count = 0;
        if (!EnumDependentServicesW(dependentService.value, SERVICE_STATE_ALL, reinterpret_cast<ENUM_SERVICE_STATUSW*>(buffer.data()),
            static_cast<DWORD>(buffer.size()), &needed, &count)) result.dependentsError = GetLastError();
        else {
            if (count > buffer.size() / sizeof(ENUM_SERVICE_STATUSW)) throw std::runtime_error("Invalid SCM dependent count");
            const auto* entries = reinterpret_cast<const ENUM_SERVICE_STATUSW*>(buffer.data());
            for (DWORD i = 0; i < count; ++i) result.dependents.push_back(stringAt(buffer, entries[i].lpServiceName));
        }
    }
    return result;
}

}
