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

std::vector<GpuEngine> gpuEngines(std::span<const NamedCounter> counters, const std::wstring& luid)
{
    std::map<std::wstring, GpuEngine> engines;
    for (const auto& item : counters) {
        const auto adapter = std::search(item.name.begin(), item.name.end(), luid.begin(), luid.end(),
            [](wchar_t a, wchar_t b) { return std::towupper(a) == std::towupper(b); });
        if (adapter == item.name.end() || (adapter != item.name.begin() && *(adapter - 1) != L'_'))
            continue;
        const auto engine = static_cast<std::size_t>(adapter - item.name.begin()) + luid.size();
        if (!item.name.substr(engine).starts_with(L"_phys_"))
            continue;
        const auto type = item.name.find(L"_engtype_", engine);
        if (type == std::wstring::npos || type + 9 == item.name.size()) continue;
        const auto id = item.name.substr(engine);
        auto [found, inserted] = engines.try_emplace(id, GpuEngine{id, item.name.substr(type + 9), {0, SampleState::valid, MetricSource::performanceCounter}});
        (void)inserted;
        auto& utilization = found->second.utilization;
        if (!item.metric.valid() || !std::isfinite(item.metric.value) || item.metric.value < 0) {
            utilization.state = SampleState::unavailable;
            continue;
        }
        utilization.value += item.metric.value;
    }
    std::vector<GpuEngine> result;
    for (auto& [id, engine] : engines) {
        (void)id;
        engine.utilization.value = std::clamp(engine.utilization.value, 0.0, 100.0);
        result.push_back(std::move(engine));
    }
    return result;
}

Metric<double> busiestGpuEngine(std::span<const NamedCounter> counters, const std::wstring& luid)
{
    const auto engines = gpuEngines(counters, luid);
    double busiest = 0;
    for (const auto& engine : engines) {
        if (!engine.utilization.valid()) return {0, SampleState::unavailable, MetricSource::performanceCounter};
        busiest = std::max(busiest, engine.utilization.value);
    }
    return {busiest, engines.empty() ? SampleState::unavailable : SampleState::valid, MetricSource::performanceCounter};
}

Metric<double> gpuMemoryUsage(std::span<const NamedCounter> counters, const std::wstring& luid)
{
    Metric<double> result{0, SampleState::unavailable, MetricSource::performanceCounter};
    bool found = false;
    for (const auto& item : counters) {
        if (item.name.size() <= luid.size() || item.name.substr(luid.size(), 6) != L"_phys_"
            || !std::equal(luid.begin(), luid.end(), item.name.begin(), [](wchar_t a, wchar_t b) { return std::towupper(a) == std::towupper(b); })) continue;
        if (!item.metric.valid() || !std::isfinite(item.metric.value) || item.metric.value < 0)
            return result;
        result.value += item.metric.value; found = true;
    }
    if (found && std::isfinite(result.value)) result.state = SampleState::valid;
    return result;
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
    cpu.measurements.primary = snapshot.measurements.systemCpu;
    cpu.measurements.capacity = measurementMetadata(MeasurementUnit::percent, MeasurementNormalization::wholeMachine, MeasurementBasis::gauge, 0);
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
    memory.measurements.primary = memory.measurements.secondary = memory.measurements.tertiary = memory.measurements.capacity
        = snapshot.measurements.systemMemory;
    return {std::move(cpu), std::move(memory)};
}

}
