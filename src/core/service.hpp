#pragma once

#include "process.hpp"
#include <windows.h>
#include <optional>

namespace nks {

enum class ServiceFilter { all, running, stopped, other };

struct ServiceConfig {
    DWORD error{}, startType{MAXDWORD}, delayedError{};
    bool delayed{};
    std::wstring account, group, command;
    std::vector<std::wstring> dependencies;
};

struct ServiceRecord {
    std::wstring name, title;
    DWORD state{}, pid{}, type{}, acceptedControls{};
    ServiceConfig config;
};

struct ServiceSnapshot {
    std::chrono::steady_clock::time_point measuredAt;
    std::chrono::nanoseconds collectionTime{};
    std::vector<ServiceRecord> services;
};

struct ServiceDetail {
    std::wstring name, description;
    ServiceConfig config;
    DWORD statusError{}, descriptionError{}, dependentsError{}, hostError{};
    DWORD pid{}, state{};
    std::uint64_t hostCreated100ns{};
    std::vector<std::wstring> dependents;
};

bool serviceMatches(const ServiceRecord& record, ServiceFilter filter, const std::wstring& query);
bool serviceNameEqual(const std::wstring& a, const std::wstring& b);
std::optional<ProcessIdentity> serviceHostIdentity(const ServiceRecord& service, const ServiceDetail& detail, const ProcessSnapshot& processes);
std::wstring serviceStatus(DWORD state);
std::wstring serviceStartup(const ServiceConfig& config);

}
