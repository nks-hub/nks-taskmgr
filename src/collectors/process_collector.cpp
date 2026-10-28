#include <phnt_windows.h>
#include <phnt.h>
#include <psapi.h>
#include "process_collector.hpp"
#include "process_parser.hpp"
#include "process_query.h"
#include "processor_query.hpp"

#include <stdexcept>
#include <vector>

namespace nks {
namespace {

LONG NTAPI queryProcesses(ULONG informationClass, void* buffer, ULONG size, ULONG* returned)
{
    return NtQuerySystemInformation(static_cast<SYSTEM_INFORMATION_CLASS>(informationClass), buffer, size, returned);
}

LONG NTAPI queryGroup(USHORT group, void* buffer, ULONG size, ULONG* returned)
{
    return NtQuerySystemInformationEx(SystemProcessorPerformanceInformation, &group, sizeof(group), buffer, size, returned);
}

}

ProcessCollector::ProcessCollector()
{
    SYSTEM_TIMEOFDAY_INFORMATION time{};
    const auto status = NtQuerySystemInformation(SystemTimeOfDayInformation, &time, sizeof(time), nullptr);
    if (!NT_SUCCESS(status) || time.BootTime.QuadPart <= 0)
        throw std::runtime_error("Cannot read boot identity");
    bootTime = static_cast<std::uint64_t>(time.BootTime.QuadPart);
}

ProcessCollector::~ProcessCollector() { NksFreeProcessBuffer(buffer); }

ProcessSnapshot ProcessCollector::collect()
{
    const auto began = std::chrono::steady_clock::now();
    ULONG used = 0;
    const auto status = NksQueryProcessBuffer(queryProcesses, &buffer, &capacity, &used);
    if (!NT_SUCCESS(status))
        throw std::runtime_error("NtQuerySystemInformation process query failed: " + std::to_string(static_cast<unsigned long>(status)));

    ProcessSnapshot result;
    result.measuredAt = std::chrono::steady_clock::now();
    result.wallTime = std::chrono::system_clock::now();
    result.logicalProcessors = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
    if (!result.logicalProcessors)
        throw std::runtime_error("No active logical processors");
    result.processes = parseProcesses({static_cast<const std::byte*>(buffer), used}, bootTime);

    std::vector<ProcessorGroup> groups;
    const auto groupCount = GetActiveProcessorGroupCount();
    for (USHORT number = 0; number < groupCount; ++number)
        groups.push_back({number, GetActiveProcessorCount(number)});
    const auto times = queryProcessorTimes(groups, queryGroup);
    result.processorState = times.state;
    result.systemIdle100ns = times.idle100ns;
    result.systemKernel100ns = times.kernel100ns;
    result.systemUser100ns = times.user100ns;

    PERFORMANCE_INFORMATION memory{sizeof(PERFORMANCE_INFORMATION)};
    if (GetPerformanceInfo(&memory, sizeof(memory))) {
        const auto page = static_cast<std::uint64_t>(memory.PageSize);
        result.memory = {memory.PhysicalTotal * page, memory.PhysicalAvailable * page, memory.CommitTotal * page,
            memory.CommitLimit * page, memory.SystemCache * page, memory.KernelPaged * page,
            memory.KernelNonpaged * page, SampleState::valid};
    }
    result.collectionTime = std::chrono::steady_clock::now() - began;
    return result;
}

}
