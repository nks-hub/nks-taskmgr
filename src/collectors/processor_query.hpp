#pragma once

#include "core/process.hpp"
#include <span>
#include <windows.h>

namespace nks {

struct ProcessorGroup {
    USHORT number{};
    ULONG activeProcessors{};
};

struct ProcessorTimes {
    std::uint64_t kernel100ns{};
    std::uint64_t user100ns{};
    std::uint64_t idle100ns{};
    SampleState state{SampleState::unavailable};
    std::vector<LogicalProcessor> processors;
};

using ProcessorGroupQuery = LONG (NTAPI*)(USHORT, void*, ULONG, ULONG*);
ProcessorTimes queryProcessorTimes(std::span<const ProcessorGroup> groups, ProcessorGroupQuery query);

/// "Rychlost" and "Základní rychlost" in whole MHz; the current speed is the nominal frequency scaled by
/// the processor performance percentage (above 100 % in turbo), as Windows Task Manager reports it.
std::vector<HardwareFact> processorSpeedFacts(double nominalMhz, double performancePercent);

/// Reads the _Total Processor Information frequency counters; the first call after construction has no rate yet.
class ProcessorSpeed {
public:
    ProcessorSpeed();
    ~ProcessorSpeed();
    ProcessorSpeed(const ProcessorSpeed&) = delete;
    ProcessorSpeed& operator=(const ProcessorSpeed&) = delete;
    std::vector<HardwareFact> collect();
private:
    void* query{};
    void* performance{};
    void* nominal{};
};

}
