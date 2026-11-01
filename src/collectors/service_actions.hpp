#pragma once
#include "service_actions_worker/protocol.hpp"
#include <windows.h>
#include <functional>
#include <memory>

namespace nks::service_actions {
struct HandleCloser { void operator()(SC_HANDLE value) const noexcept { if (value) CloseServiceHandle(value); } };
using Handle = std::unique_ptr<std::remove_pointer_t<SC_HANDLE>, HandleCloser>;
struct Configuration {
    DWORD type{}, startType{}, errorControl{}, tag{}; bool delayed{};
    std::wstring title, command, account, group;
    std::vector<std::wstring> dependencies;
    bool operator==(const Configuration&) const = default;
};
struct Node {
    std::wstring name; Handle handle; Configuration config; SERVICE_STATUS_PROCESS original{};
    std::vector<std::uint32_t> dependents;
    std::uint32_t flags{}, stopOrder{UINT32_MAX}, startOrder{UINT32_MAX};
};
struct Plan {
    Action action{}; Handle manager; std::vector<Node> nodes; std::vector<std::uint32_t> order;
    std::uint64_t nonceLow{}, nonceHigh{}, createdAt{};
};
struct ApplyResult { Status status{}; DWORD error{}; std::vector<ResultRow> rows; };
class Failure : public std::runtime_error {
public:
    Failure(Status status, DWORD error, const char* message) : std::runtime_error(message), status(status), error(error) {}
    Status status; DWORD error;
};
struct Dependent { std::wstring name; DWORD state{}; };
[[noreturn]] void fail(DWORD error, const char* message);
Configuration readConfiguration(SC_HANDLE service);
SERVICE_STATUS_PROCESS readStatus(SC_HANDLE service);
std::vector<Dependent> activeDependents(SC_HANDLE service);
void verifyName(const Plan& plan, const Node& node);
bool nameEqual(const std::wstring& a, const std::wstring& b);
Plan makePlan(Action action, const std::wstring& name);
ApplyResult applyPlan(Plan& plan, std::span<const std::uint32_t> approved, const std::function<void(const std::vector<ResultRow>&)>& progress);
}
