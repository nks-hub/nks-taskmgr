#include <phnt_windows.h>
#include <phnt.h>
#include "process_tree_actions.hpp"
#include "process_parser.hpp"
#include "process_query.h"
#include <algorithm>
#include <chrono>
#include <unordered_map>
#include <unordered_set>

namespace nks::actions {
namespace {
using Clock = std::chrono::steady_clock;
constexpr auto budget = std::chrono::seconds(4);

LONG NTAPI query(ULONG kind, void* bytes, ULONG size, ULONG* used)
{
    return NtQuerySystemInformation(static_cast<SYSTEM_INFORMATION_CLASS>(kind), bytes, size, used);
}

std::vector<ProcessRecord> census(BootIdentifier boot)
{
    void* bytes = nullptr; ULONG size = 0, used = 0;
    // Bound this inventory to 8 MiB without changing the shared query helper's global cap.
    NksProcessQueryBudget limits{4, NKS_PROCESS_BUFFER_LIMIT - 8 * 1024 * 1024};
    const auto status = NksQueryProcessBufferBudgeted(query, SystemProcessInformation, &bytes, &size, &used, &limits);
    struct Buffer { void* data; ~Buffer() { NksFreeProcessBuffer(data); } } owner{bytes};
    if (!NT_SUCCESS(status)) throw std::runtime_error("Process tree inventory is unavailable");
    return parseProcesses({static_cast<const std::byte*>(bytes), used}, boot);
}

bool rawTime(HANDLE process, std::uint64_t& value)
{
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process, &created, &exited, &kernel, &user)) return false;
    value = (static_cast<std::uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
    return value != 0;
}

bool alive(const TreeNode& node)
{
    if (!node.process || WaitForSingleObject(node.process.get(), 0) != WAIT_TIMEOUT) return false;
    const auto actual = queryProcessIdentity(node.process.get(), node.result.target.key.generationKind);
    std::uint64_t created = 0;
    return actual.state == SampleState::valid && actual.identity == node.result.target.key && rawTime(node.process.get(), created)
        && created == node.result.target.created100ns;
}

void discover(TreeResult& tree, const std::vector<ProcessRecord>& records, Clock::time_point began)
{
    std::unordered_map<std::uint32_t, const ProcessRecord*> byPid;
    for (const auto& record : records) if (!byPid.emplace(record.identity.pid, &record).second) throw std::runtime_error("Ambiguous process inventory");
    const auto known = [&](const ProcessRecord& record) {
        return std::any_of(tree.nodes.begin(), tree.nodes.end(), [&](const auto& node) {
            return node.result.target.key.pid == record.identity.pid && node.result.target.created100ns == record.created100ns;
        });
    };
    for (std::size_t parentIndex = 0; parentIndex < tree.nodes.size(); ++parentIndex) {
        if (Clock::now() - began >= budget) { tree.flags |= timeLimit; return; }
        const auto target = tree.nodes[parentIndex].result.target;
        const auto snapshot = byPid.find(target.key.pid);
        const bool verified = !(tree.nodes[parentIndex].result.flags & mayHaveApplied) && alive(tree.nodes[parentIndex])
            && snapshot != byPid.end() && snapshot->second->created100ns == target.created100ns;
        for (const auto& child : records) {
            if (child.parentPid != target.key.pid || child.identity.pid == target.key.pid || known(child)) continue;
            if (!verified) { tree.flags |= parentLost | residual; ++tree.residualCount; continue; }
            if (!child.created100ns || child.created100ns < target.created100ns) { tree.flags |= parentLost | residual; ++tree.residualCount; continue; }
            if (tree.nodes.size() == maximumTreeTargets) { tree.flags |= targetLimit | residual; ++tree.residualCount; continue; }
            TreeNode node;
            node.parent = static_cast<std::uint32_t>(parentIndex); node.depth = tree.nodes[parentIndex].depth + 1;
            node.result.target = {child.identity, child.created100ns, 0};
            node.result.outcome = Outcome::notPerformed; node.result.reason = Reason::treeBudget;
            node.process.reset(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE | PROCESS_TERMINATE, FALSE, child.identity.pid));
            if (!node.process) {
                node.result.outcome = Outcome::refused; node.result.error = GetLastError();
                node.result.reason = node.result.error == ERROR_ACCESS_DENIED ? Reason::accessDenied : Reason::identityUnverifiable;
            } else {
                const auto actual = queryProcessIdentity(node.process.get(), tree.root.key.generationKind);
                std::uint64_t created = 0;
                if (actual.state == SampleState::valid && actual.identity.boot == tree.root.key.boot && actual.identity.pid == child.identity.pid
                    && rawTime(node.process.get(), created) && created == child.created100ns) node.result.target.key = actual.identity;
                else {
                    node.result.outcome = Outcome::identityChanged; node.result.reason = Reason::treeBoundary; node.result.error = ERROR_RETRY;
                    node.process.reset(); tree.flags |= parentLost;
                }
            }
            if (!alive(tree.nodes[parentIndex])) { tree.flags |= parentLost | residual; ++tree.residualCount; continue; }
            tree.nodes.push_back(std::move(node));
        }
    }
}

void execute(const Context& context, TreeNode& node, std::uint32_t& order)
{
    if (!node.process || node.result.outcome != Outcome::notPerformed) return;
    const auto target = node.result.target;
    node.result = Result{}; node.result.target = target;
    node.execution = ++order;
    try { applyHeldAction(context, Command::terminate, 0, 0, node.process.get(), node.result); }
    catch (...) {
        node.result.outcome = node.result.flags & mayHaveApplied ? Outcome::unverified : Outcome::error;
        node.result.reason = Reason::nativeUnavailable; node.result.error = ERROR_GEN_FAILURE;
        node.result.flags &= ~static_cast<std::uint32_t>(effectConfirmed);
    }
    if (!(node.result.flags & mayHaveApplied)) node.execution = 0;
}
}

TreeResult terminateProcessTree(const Context& context, const Target& root, const PublishTreePlan& publish, HANDLE heldRoot)
{
    const auto began = Clock::now();
    TreeResult tree; tree.root = root; tree.nodes.reserve(maximumTreeTargets);
    TreeNode first; first.result.target = root;
    if (heldRoot) {
        HANDLE copy = nullptr;
        if (DuplicateHandle(GetCurrentProcess(), heldRoot, GetCurrentProcess(), &copy, 0, FALSE, DUPLICATE_SAME_ACCESS)) first.process.reset(copy);
    } else first.process.reset(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE | PROCESS_TERMINATE, FALSE, root.key.pid));
    if (first.process) applyHeldAction(context, Command::query, 0, 0, first.process.get(), first.result);
    else {
        first.result.outcome = Outcome::refused; first.result.error = GetLastError();
        first.result.reason = first.result.error == ERROR_ACCESS_DENIED ? Reason::accessDenied : Reason::identityUnverifiable;
    }
    const bool validRoot = first.result.outcome == Outcome::inspected;
    if (validRoot) { first.result = Result{}; first.result.target = root; first.result.outcome = Outcome::notPerformed; first.result.reason = Reason::treeBudget; }
    tree.nodes.push_back(std::move(first));
    std::uint32_t order = 0;
    if (validRoot) for (unsigned pass = 0; pass < 2; ++pass) {
        if (Clock::now() - began >= budget) { tree.flags |= timeLimit; break; }
        if (!alive(tree.nodes[0])) { tree.flags |= parentLost; break; }
        try { ++tree.passes; discover(tree, census(root.key.boot), began); }
        catch (...) { tree.flags |= censusFailed; break; }
        ++tree.ordinal;
        if (!publish(tree)) { tree.flags |= timeLimit; return tree; }
        std::vector<std::size_t> sequence;
        for (std::size_t i = 1; i < tree.nodes.size(); ++i) sequence.push_back(i);
        std::sort(sequence.begin(), sequence.end(), [&](auto a, auto b) { return tree.nodes[a].depth > tree.nodes[b].depth; });
        for (const auto index : sequence) {
            if (Clock::now() - began >= budget) { tree.flags |= timeLimit; break; }
            execute(context, tree.nodes[index], order);
        }
    }
    ++tree.ordinal;
    if (!publish(tree)) return tree;
    if (validRoot && Clock::now() - began < budget) execute(context, tree.nodes[0], order);
    else if (validRoot) tree.flags |= timeLimit;
    if (validRoot && Clock::now() - began < budget) {
        try {
            ++tree.passes;
            const auto records = census(root.key.boot);
            tree.residualCount = 0;
            std::unordered_set<std::uint32_t> parents;
            for (auto& node : tree.nodes) {
                parents.insert(node.result.target.key.pid);
                if (node.process && WaitForSingleObject(node.process.get(), 0) == WAIT_OBJECT_0) {
                    node.result.flags &= ~static_cast<std::uint32_t>(aliveAfter); node.result.flags |= exitedAfter;
                    if (node.result.outcome == Outcome::terminationRequested) { node.result.outcome = Outcome::applied; node.result.flags |= effectConfirmed; }
                }
            }
            for (const auto& record : records) {
                const auto known = std::find_if(tree.nodes.begin(), tree.nodes.end(), [&](const auto& node) { return node.result.target.key.pid == record.identity.pid && node.result.target.created100ns == record.created100ns; });
                if (known == tree.nodes.end() && parents.contains(record.parentPid)) { tree.flags |= residual | parentLost; ++tree.residualCount; }
            }
        } catch (...) { tree.flags |= censusFailed; }
    } else if (validRoot) tree.flags |= timeLimit;
    if (tree.passes == 3) tree.flags |= passLimit;
    for (auto& node : tree.nodes) if (node.result.outcome == Outcome::notPerformed) {
        node.result.reason = tree.flags & timeLimit ? Reason::treeBudget : tree.flags & censusFailed ? Reason::treeCensus : Reason::treeUncertain;
        node.result.error = tree.flags & timeLimit ? ERROR_TIMEOUT : ERROR_RETRY;
    }
    return tree;
}
}
