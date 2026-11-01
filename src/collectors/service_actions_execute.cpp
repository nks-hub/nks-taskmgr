#include "service_actions.hpp"
#include <algorithm>
#include <chrono>

namespace nks::service_actions {
namespace {
void verifyConfiguration(const Plan& plan, const Node& node) {
    verifyName(plan, node);
    if (readConfiguration(node.handle.get()) != node.config)
        throw Failure(Status::changed, ERROR_REVISION_MISMATCH, "Service configuration changed after confirmation");
}
void observe(const Node& node, ResultRow& row) {
    try {
        const auto status = readStatus(node.handle.get());
        row.lastState = status.dwCurrentState; row.checkpoint = status.dwCheckPoint; row.waitHint = status.dwWaitHint;
        row.win32Exit = status.dwWin32ExitCode; row.serviceExit = status.dwServiceSpecificExitCode;
        row.observationError = 0; row.flags |= stateKnown;
    } catch (const Failure& error) {
        row.lastState = row.checkpoint = row.waitHint = row.win32Exit = row.serviceExit = 0;
        row.flags &= ~stateKnown; row.observationError = error.error; throw;
    }
    row.observedAtNs = std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
void checkDeadline(std::uint64_t deadline) {
    if (GetTickCount64() >= deadline) throw Failure(Status::timeout, ERROR_TIMEOUT, "Service operation deadline elapsed");
}
void verifyDependents(const Plan& plan, const Node& node) {
    const auto current = activeDependents(node.handle.get());
    if (current.size() != node.dependents.size()) throw Failure(Status::changed, ERROR_DEPENDENT_SERVICES_RUNNING, "Active dependents changed after planning");
    for (const auto& value : current) {
        bool found = false;
        for (const auto index : node.dependents) if (nameEqual(value.name, plan.nodes[index].name)
            && value.state == plan.nodes[index].original.dwCurrentState) { found = true; break; }
        if (!found) throw Failure(Status::changed, ERROR_DEPENDENT_SERVICES_RUNNING, "Unapproved or changed service dependent");
    }
}
void waitFor(Plan& plan, std::uint32_t id, bool starting, std::uint64_t deadline, std::vector<ResultRow>& rows,
    const std::function<void(const std::vector<ResultRow>&)>& progress)
{
    auto& row = rows[id]; const auto& node = plan.nodes[id];
    DWORD checkpoint = 0; auto advancedAt = GetTickCount64();
    for (;;) {
        checkDeadline(deadline); observe(node, row); checkDeadline(deadline);
        progress(rows);
        const DWORD desired = starting ? DWORD{SERVICE_RUNNING} : DWORD{SERVICE_STOPPED};
        if (row.lastState == desired) {
            verifyConfiguration(plan, node); checkDeadline(deadline); row.flags |= configVerified;
            row.flags |= starting ? startConfirmed : stopConfirmed; return;
        }
        if (row.lastState != (starting ? DWORD{SERVICE_START_PENDING} : DWORD{SERVICE_STOP_PENDING}))
            throw Failure(Status::error, row.win32Exit ? row.win32Exit : ERROR_SERVICE_CANNOT_ACCEPT_CTRL, "Service did not reach the requested state");
        const auto now = GetTickCount64();
        if (row.checkpoint > checkpoint) { checkpoint = row.checkpoint; advancedAt = now; }
        if (now - advancedAt > std::clamp<std::uint64_t>(row.waitHint, 1000, 30000))
            throw Failure(Status::timeout, ERROR_SERVICE_REQUEST_TIMEOUT, "Service checkpoint did not advance");
        const auto wait = (std::min)(std::clamp<std::uint64_t>(row.waitHint / 10, 250, 1000), deadline - now);
        Sleep(static_cast<DWORD>(wait));
    }
}
}

ApplyResult applyPlan(Plan& plan, std::span<const std::uint32_t> approved,
    const std::function<void(const std::vector<ResultRow>&)>& progress)
{
    ApplyResult result; result.rows.resize(plan.nodes.size());
    for (std::uint32_t i = 0; i < result.rows.size(); ++i) result.rows[i].id = i;
    if (GetTickCount64() - plan.createdAt > 60000) { result.status = Status::changed; result.error = ERROR_TIMEOUT; return result; }
    std::vector<bool> allowed(plan.nodes.size());
    for (const auto id : approved) {
        if (id == 0 || id >= allowed.size() || allowed[id] || !(plan.nodes[id].flags & stopRequired)) {
            result.status = Status::invalidRequest; result.error = ERROR_INVALID_PARAMETER; return result;
        }
        allowed[id] = true;
    }
    for (std::uint32_t i = 1; i < plan.nodes.size(); ++i) if (allowed[i] != ((plan.nodes[i].flags & stopRequired) != 0)) {
        result.status = Status::invalidRequest; result.error = ERROR_INVALID_PARAMETER; return result;
    }
    const auto deadline = GetTickCount64() + 30000;
    std::uint32_t active = 0;
    try {
        // Verify the complete immutable plan before the first mutating call.
        for (std::uint32_t i = 0; i < plan.nodes.size(); ++i) {
            active = i; checkDeadline(deadline); verifyConfiguration(plan, plan.nodes[i]);
            result.rows[i].flags |= configVerified; observe(plan.nodes[i], result.rows[i]);
            if (result.rows[i].lastState != plan.nodes[i].original.dwCurrentState)
                throw Failure(Status::changed, ERROR_RETRY, "Service state changed after planning");
            if (plan.action != Action::start) verifyDependents(plan, plan.nodes[i]);
        }
        progress(result.rows);
        for (const auto id : plan.order) {
            active = id; auto& node = plan.nodes[id]; auto& row = result.rows[id];
            if (!(node.flags & stopRequired)) continue;
            checkDeadline(deadline); verifyConfiguration(plan, node); observe(node, row);
            if (row.lastState != SERVICE_STOPPED) {
                if (row.lastState != node.original.dwCurrentState) throw Failure(Status::changed, ERROR_RETRY, "Service state changed before stop");
                // A newly active dependent is never stopped implicitly or retried.
                if (!activeDependents(node.handle.get()).empty()) throw Failure(Status::changed, ERROR_DEPENDENT_SERVICES_RUNNING, "A dependent is still active");
                checkDeadline(deadline); row.flags |= stopRequested | mayHaveApplied; progress(result.rows);
                SERVICE_STATUS status{};
                if (!ControlService(node.handle.get(), SERVICE_CONTROL_STOP, &status)) {
                    row.stopError = GetLastError();
                    if (row.stopError != ERROR_SERVICE_NOT_ACTIVE) fail(row.stopError, "Service stop request failed");
                }
            }
            waitFor(plan, id, false, deadline, result.rows, progress);
        }
        // This is the explicitly confirmed restart phase, never a cleanup retry.
        for (auto at = plan.order.rbegin(); at != plan.order.rend(); ++at) {
            active = *at; auto& node = plan.nodes[active]; auto& row = result.rows[active];
            if (!(node.flags & startRequired)) continue;
            checkDeadline(deadline); verifyConfiguration(plan, node); observe(node, row);
            if (row.lastState != SERVICE_RUNNING) {
                if (row.lastState != SERVICE_STOPPED) throw Failure(Status::changed, ERROR_RETRY, "Service state changed before start");
                checkDeadline(deadline); row.flags |= startRequested | mayHaveApplied; progress(result.rows);
                // SCM itself resolves configured service/group dependencies.
                if (!StartServiceW(node.handle.get(), 0, nullptr)) {
                    row.startError = GetLastError();
                    if (row.startError != ERROR_SERVICE_ALREADY_RUNNING) fail(row.startError, "Service start request failed");
                }
            }
            waitFor(plan, active, true, deadline, result.rows, progress);
        }
        for (std::uint32_t i = 0; i < plan.nodes.size(); ++i) {
            active = i; auto& row = result.rows[i]; const auto& node = plan.nodes[i];
            checkDeadline(deadline); verifyConfiguration(plan, node); observe(node, row); checkDeadline(deadline);
            if (row.lastState != (plan.action == Action::stop ? DWORD{SERVICE_STOPPED} : DWORD{SERVICE_RUNNING}))
                throw Failure(Status::changed, ERROR_RETRY, "Final service state changed");
            row.outcome = node.flags & (stopRequired | startRequired) ? Outcome::confirmed : Outcome::alreadyDesired;
        }
    } catch (const Failure& failure) {
        result.status = failure.status; result.error = failure.error;
        auto& row = result.rows[active]; row.observationError = failure.error;
        row.outcome = failure.status == Status::changed ? Outcome::changed : failure.status == Status::timeout ? Outcome::timeout : Outcome::error;
        // No further mutation follows a failure; the UI must show the incomplete restart.
        for (auto& other : result.rows) if (other.id != active && other.outcome == Outcome::notAttempted && (other.flags & mayHaveApplied)) other.outcome = Outcome::unknown;
        if (std::any_of(result.rows.begin(), result.rows.end(), [](const auto& value) { return (value.flags & mayHaveApplied) != 0; })) result.status = Status::partial;
    }
    return result;
}
}
