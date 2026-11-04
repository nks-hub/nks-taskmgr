#include "process_delta.hpp"
#include <limits>
#include <stdexcept>
#include <unordered_map>

namespace nks {
namespace {
bool metricEqual(const Metric<double>& left, const Metric<double>& right) {
    return left.value == right.value && left.state == right.state && left.source == right.source;
}
bool equal(const ProcessRecord& left, const ProcessRecord& right) {
    const auto& a = left.counters; const auto& b = right.counters;
    return left.identity == right.identity && left.parentPid == right.parentPid && left.name == right.name
        && left.created100ns == right.created100ns && left.sessionId == right.sessionId && a.kernel100ns == b.kernel100ns && a.user100ns == b.user100ns
        && a.readIoBytes == b.readIoBytes && a.writeIoBytes == b.writeIoBytes && a.workingSetBytes == b.workingSetBytes
        && a.privateBytes == b.privateBytes && a.threads == b.threads && a.handles == b.handles
        && metricEqual(left.cpuPercent, right.cpuPercent) && metricEqual(left.readIoBytesPerSecond, right.readIoBytesPerSecond)
        && metricEqual(left.writeIoBytesPerSecond, right.writeIoBytesPerSecond);
}
ProcessChange change(ProcessChangeKind kind, const ProcessRecord& row, std::uint32_t index) {
    return {kind, index, row.identity.pid, row.identity.generationKind, row.identity.generation};
}
}

ProcessChanges processChanges(const ProcessSnapshot* before, const ProcessSnapshot& current)
{
    if (current.processes.size() > 131072) throw std::length_error("Process delta exceeds limit");
    ProcessChanges result;
    result.reset = !before || before->boot.state != SampleState::valid || current.boot.state != SampleState::valid
        || current.boot.id.empty() || before->boot.id != current.boot.id;
    std::unordered_map<std::uint32_t, const ProcessRecord*> old;
    if (!result.reset) {
        old.reserve(before->processes.size());
        for (const auto& row : before->processes) if (!old.emplace(row.identity.pid, &row).second) { result.reset = true; break; }
    }
    std::unordered_map<std::uint32_t, const ProcessRecord*> now; now.reserve(current.processes.size());
    for (const auto& row : current.processes) if (!now.emplace(row.identity.pid, &row).second) { result.reset = true; break; }
    result.entries.reserve(current.processes.size());
    for (std::uint32_t i = 0; i < current.processes.size(); ++i) {
        const auto& row = current.processes[i];
        const auto found = old.find(row.identity.pid);
        if (result.reset || found == old.end() || found->second->identity != row.identity)
            result.entries.push_back(change(ProcessChangeKind::added, row, i));
        else if (!equal(*found->second, row)) result.entries.push_back(change(ProcessChangeKind::updated, row, i));
    }
    if (!result.reset) for (const auto& row : before->processes) {
        const auto found = now.find(row.identity.pid);
        if (found == now.end() || found->second->identity != row.identity)
            result.entries.push_back(change(ProcessChangeKind::removed, row, UINT32_MAX));
    }
    return result;
}

}
