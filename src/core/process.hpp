#pragma once
#include "hardware.hpp"

#include <chrono>
#include <cstdint>
#include <string>
#include <vector>

namespace nks {

enum class SampleState : std::uint8_t {
    valid, first, unavailable, accessDenied, unsupported, stale, error, gap
};

enum class MetricSource : std::uint8_t {
    nativeProcess, processorTime, performanceInfo, performanceCounter, networkInterface, dxgi, gpuAdapter
};

template<class T>
struct Metric {
    T value{};
    SampleState state{SampleState::first};
    MetricSource source{MetricSource::nativeProcess};

    bool valid() const noexcept { return state == SampleState::valid; }
};

enum class GenerationKind : std::uint8_t { missing, creationTime, sequenceNumber };

struct BootIdentifier {
    std::uint64_t low{}, high{};
    bool empty() const noexcept { return low == 0 && high == 0; }
    bool operator==(const BootIdentifier&) const = default;
};

struct BootIdentity {
    BootIdentifier id;
    SampleState state{SampleState::unavailable};
};

struct ProcessIdentity {
    BootIdentifier boot;
    std::uint32_t pid{};
    GenerationKind generationKind{GenerationKind::missing};
    std::uint64_t generation{};
    bool operator==(const ProcessIdentity&) const = default;
};

struct ProcessCounters {
    std::uint64_t kernel100ns{};
    std::uint64_t user100ns{};
    std::uint64_t readIoBytes{};
    std::uint64_t writeIoBytes{};
    std::uint64_t workingSetBytes{};
    std::uint64_t privateBytes{};
    std::uint32_t threads{};
    std::uint32_t handles{};
};

struct ProcessRecord {
    ProcessIdentity identity;
    std::uint32_t parentPid{};
    std::wstring name;
    ProcessCounters counters;
    Metric<double> cpuPercent;
    Metric<double> readIoBytesPerSecond;
    Metric<double> writeIoBytesPerSecond;
    std::uint64_t created100ns{};
};

struct SystemMemory {
    std::uint64_t physicalBytes{};
    std::uint64_t availableBytes{};
    std::uint64_t committedBytes{};
    std::uint64_t commitLimitBytes{};
    std::uint64_t systemCacheBytes{};
    std::uint64_t pagedPoolBytes{};
    std::uint64_t nonpagedPoolBytes{};
    SampleState state{SampleState::unavailable};
    std::uint64_t pageFileBytes{}, pageFileUsedBytes{}, pageFilePeakBytes{};
    SampleState pageFileState{SampleState::unavailable};
    std::uint64_t installedBytes{};
};

struct LogicalProcessor {
    std::uint16_t group{}, index{};
    std::uint64_t kernel100ns{}, user100ns{}, idle100ns{};
    Metric<double> cpuPercent{0, SampleState::first, MetricSource::processorTime};
};

struct ProcessSnapshot {
    BootIdentity boot;
    std::uint64_t bootTime{};
    std::vector<HardwareFact> memoryFacts, cpuFacts, boardFacts;
    std::chrono::steady_clock::time_point measuredAt;
    std::chrono::system_clock::time_point wallTime;
    std::chrono::nanoseconds collectionTime{};
    std::chrono::nanoseconds queryTime{}, parseTime{}, systemTime{};
    std::uint32_t logicalProcessors{};
    std::wstring cpuModel;
    std::vector<LogicalProcessor> processors;
    std::uint64_t systemKernel100ns{};
    std::uint64_t systemUser100ns{};
    std::uint64_t systemIdle100ns{};
    SampleState processorState{SampleState::unavailable};
    Metric<double> systemCpuPercent{0, SampleState::first, MetricSource::processorTime};
    SystemMemory memory;
    std::vector<ProcessRecord> processes;
};

}
