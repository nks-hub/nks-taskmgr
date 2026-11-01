#include "service_actions.hpp"
#include <objbase.h>
#include <array>

namespace nks::service_actions {
Plan makePlan(Action action, const std::wstring& name)
{
    if (action < Action::start || action > Action::restart || name.empty() || name.size() > 256 || !validText(name))
        throw Failure(Status::invalidRequest, ERROR_INVALID_PARAMETER, "Invalid service action target");
    Plan plan; plan.action = action; plan.nodes.reserve(maximumServices);
    plan.manager.reset(OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT));
    if (!plan.manager) fail(GetLastError(), "Cannot open service manager");
    const DWORD access = SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS
        | (action == Action::start ? SERVICE_START : SERVICE_STOP | SERVICE_ENUMERATE_DEPENDENTS)
        | (action == Action::restart ? SERVICE_START : 0);
    const auto add = [&](const std::wstring& serviceName) {
        if (plan.nodes.size() == maximumServices) throw Failure(Status::limitExceeded, ERROR_MORE_DATA, "Service plan exceeds 64 services");
        Node node; node.name = serviceName; node.handle.reset(OpenServiceW(plan.manager.get(), serviceName.c_str(), access));
        if (!node.handle) fail(GetLastError(), "Cannot open planned service");
        node.config = readConfiguration(node.handle.get()); node.original = readStatus(node.handle.get());
        if (!(node.config.type & SERVICE_WIN32)) throw Failure(Status::unsupported, ERROR_NOT_SUPPORTED, "Driver service actions are unsupported");
        const auto state = node.original.dwCurrentState;
        if (state != SERVICE_RUNNING && state != SERVICE_STOPPED && !(action == Action::stop && state == SERVICE_PAUSED))
            throw Failure(Status::unsupported, ERROR_SERVICE_CANNOT_ACCEPT_CTRL, "Service is pending or paused; wait before planning this action");
        if (plan.nodes.empty()) node.flags |= PlanFlag::root;
        if (state == SERVICE_RUNNING) node.flags |= initiallyRunning;
        if (action != Action::start && state != SERVICE_STOPPED) {
            if (!(node.original.dwControlsAccepted & SERVICE_ACCEPT_STOP))
                throw Failure(Status::unsupported, ERROR_INVALID_SERVICE_CONTROL, "Service does not accept stop control");
            node.flags |= stopRequired;
        }
        if ((action == Action::start && state != SERVICE_RUNNING) || action == Action::restart && (plan.nodes.empty() || state == SERVICE_RUNNING)) node.flags |= startRequired;
        plan.nodes.push_back(std::move(node)); return static_cast<std::uint32_t>(plan.nodes.size() - 1);
    };
    add(name);
    std::array<unsigned char, maximumServices> color{};
    const auto visit = [&](auto&& self, std::uint32_t index) -> void {
        if (color[index] == 2) return;
        if (color[index] == 1) throw Failure(Status::changed, ERROR_CIRCULAR_DEPENDENCY, "Service dependency cycle");
        color[index] = 1;
        if (action != Action::start) {
            const auto dependents = activeDependents(plan.nodes[index].handle.get());
            for (const auto& dependent : dependents) {
                std::uint32_t child = static_cast<std::uint32_t>(plan.nodes.size());
                for (std::uint32_t i = 0; i < plan.nodes.size(); ++i) if (nameEqual(plan.nodes[i].name, dependent.name)) { child = i; break; }
                if (child == plan.nodes.size()) child = add(dependent.name);
                if (plan.nodes[child].original.dwCurrentState != dependent.state || dependent.state == SERVICE_STOPPED)
                    throw Failure(Status::changed, ERROR_RETRY, "Dependent service changed during planning");
                plan.nodes[index].dependents.push_back(child); self(self, child);
            }
        }
        color[index] = 2; plan.order.push_back(index);
    };
    visit(visit, 0);
    for (std::uint32_t i = 0; i < plan.order.size(); ++i) {
        auto& node = plan.nodes[plan.order[i]];
        if (node.flags & stopRequired) node.stopOrder = i;
        if (node.flags & startRequired) node.startOrder = static_cast<std::uint32_t>(plan.order.size() - i - 1);
    }
    GUID nonce{};
    if (FAILED(CoCreateGuid(&nonce))) fail(ERROR_NOT_ENOUGH_MEMORY, "Cannot create service plan nonce");
    static_assert(sizeof(nonce) == 16);
    std::memcpy(&plan.nonceLow, &nonce, 8); std::memcpy(&plan.nonceHigh, reinterpret_cast<const BYTE*>(&nonce) + 8, 8);
    plan.createdAt = GetTickCount64(); return plan;
}
}
