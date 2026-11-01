#pragma once

#include "process.hpp"
#include <array>
#include <map>
#include <span>

namespace nks {

enum class ResourceKind { cpu, memory, disk, gpu, network };

struct GpuEngine {
    std::wstring id, name;
    Metric<double> utilization{0, SampleState::unavailable, MetricSource::performanceCounter};
};

struct ResourceRecord {
    std::vector<HardwareFact> facts;
    std::wstring id;
    ResourceKind kind{};
    std::wstring title;
    std::wstring description;
    std::wstring status;
    Metric<double> primary{0, SampleState::unavailable, MetricSource::performanceCounter};
    Metric<double> secondary{0, SampleState::unavailable, MetricSource::performanceCounter};
    Metric<double> tertiary{0, SampleState::unavailable, MetricSource::performanceCounter};
    double capacity{};
    double sharedCapacity{};
    std::vector<GpuEngine> engines;
    Metric<double> temperature{0, SampleState::unavailable, MetricSource::gpuAdapter};
    ResourceMeasurements measurements;
};

struct ResourceSnapshot {
    std::vector<HardwareFact> upsFacts;
    std::vector<HardwareFact> cpuSensorFacts;
    std::chrono::steady_clock::time_point measuredAt;
    std::vector<ResourceRecord> devices;
    bool diskCountersAvailable{};
    bool gpuCountersAvailable{};
};

struct NamedCounter {
    std::wstring name;
    Metric<double> metric;
};

Metric<double> networkRate(std::uint64_t current, std::uint64_t previous, double seconds);
Metric<double> busiestGpuEngine(std::span<const NamedCounter> counters, const std::wstring& luid);
std::vector<GpuEngine> gpuEngines(std::span<const NamedCounter> counters, const std::wstring& luid);
Metric<double> gpuMemoryUsage(std::span<const NamedCounter> counters, const std::wstring& luid);
std::vector<ResourceRecord> systemResources(const ProcessSnapshot& snapshot);

}
