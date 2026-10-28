#include "resource.hpp"
#include <algorithm>
#include <cmath>
#include <cwctype>

namespace nks {

Metric<double> networkRate(std::uint64_t current, std::uint64_t previous, double seconds)
{
    if (seconds <= 0 || seconds > 30 || current < previous)
        return {0, SampleState::gap, MetricSource::networkInterface};
    return {static_cast<double>(current - previous) * 8 / seconds, SampleState::valid, MetricSource::networkInterface};
}

Metric<double> busiestGpuEngine(std::span<const NamedCounter> counters, const std::wstring& luid)
{
    std::map<std::wstring, double> engines;
    bool invalid = false;
    for (const auto& item : counters) {
        const auto adapter = std::search(item.name.begin(), item.name.end(), luid.begin(), luid.end(),
            [](wchar_t a, wchar_t b) { return std::towupper(a) == std::towupper(b); });
        if (adapter == item.name.end())
            continue;
        const auto engine = item.name.find(L"_phys_", static_cast<std::size_t>(adapter - item.name.begin()) + luid.size());
        if (engine == std::wstring::npos)
            continue;
        if (!item.metric.valid() || !std::isfinite(item.metric.value) || item.metric.value < 0) {
            invalid = true;
            continue;
        }
        engines[item.name.substr(engine)] += item.metric.value;
    }
    if (invalid || engines.empty())
        return {0, SampleState::unavailable, MetricSource::performanceCounter};
    double busiest = 0;
    for (const auto& [name, percent] : engines) {
        (void)name;
        busiest = std::max(busiest, percent);
    }
    return {std::clamp(busiest, 0.0, 100.0), SampleState::valid, MetricSource::performanceCounter};
}

std::vector<ResourceRecord> systemResources(const ProcessSnapshot& snapshot)
{
    ResourceRecord cpu;
    cpu.id = L"cpu";
    cpu.kind = ResourceKind::cpu;
    cpu.title = L"Procesor";
    cpu.description = snapshot.cpuModel.empty() ? std::to_wstring(snapshot.logicalProcessors) + L" logických procesorů" : snapshot.cpuModel;
    cpu.primary = snapshot.systemCpuPercent;
    cpu.capacity = 100;
    ResourceRecord memory;
    memory.id = L"memory";
    memory.kind = ResourceKind::memory;
    memory.title = L"Paměť";
    memory.description = L"Fyzická paměť";
    memory.capacity = static_cast<double>(snapshot.memory.physicalBytes);
    memory.primary = {0, snapshot.memory.state, MetricSource::performanceInfo};
    if (snapshot.memory.state == SampleState::valid && snapshot.memory.availableBytes <= snapshot.memory.physicalBytes)
        memory.primary.value = static_cast<double>(snapshot.memory.physicalBytes - snapshot.memory.availableBytes);
    else if (snapshot.memory.state == SampleState::valid)
        memory.primary.state = SampleState::error;
    memory.secondary = {static_cast<double>(snapshot.memory.availableBytes), memory.primary.state, MetricSource::performanceInfo};
    memory.tertiary = {static_cast<double>(snapshot.memory.committedBytes), memory.primary.state, MetricSource::performanceInfo};
    return {std::move(cpu), std::move(memory)};
}

}
