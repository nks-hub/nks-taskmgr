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

struct NetworkTransfer {
    std::uint64_t receivedBytes{}, sentBytes{};
    std::int64_t startedAtNs{}, startedAtUnixNs{};
    SampleState state{SampleState::unavailable};
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
    // Raw interface octet counters from the collector; octetsValid is false when this sample has none.
    std::uint64_t receivedOctets{}, sentOctets{};
    bool octetsValid{};
    NetworkTransfer transfer;
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

// Totals since the first observation of each network adapter. A counter decrease, an unavailable sample or
// a missing adapter restarts that adapter's baseline, so totals never decrease or jump across the gap.
class NetworkTransferTotals {
public:
    void apply(ResourceSnapshot& snapshot, std::int64_t wallTimeUnixNs);
private:
    struct Adapter {
        std::uint64_t received{}, sent{};
        bool continuous{};
        NetworkTransfer totals;
    };
    std::map<std::wstring, Adapter> adapters;
};

Metric<double> networkRate(std::uint64_t current, std::uint64_t previous, double seconds);
Metric<double> busiestGpuEngine(std::span<const NamedCounter> counters, const std::wstring& luid);
std::vector<GpuEngine> gpuEngines(std::span<const NamedCounter> counters, const std::wstring& luid);
Metric<double> gpuMemoryUsage(std::span<const NamedCounter> counters, const std::wstring& luid);
std::vector<ResourceRecord> systemResources(const ProcessSnapshot& snapshot);

}
