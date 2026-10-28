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

}
