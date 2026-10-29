#include <phnt_windows.h>
#include <phnt.h>
#include <psapi.h>
#include "process_collector.hpp"
#include "process_parser.hpp"
#include "process_query.h"
#include "processor_query.hpp"
#include "boot_identity.hpp"

#include <stdexcept>
#include <vector>
#include <intrin.h>
#include <array>
#include <cstring>

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

std::wstring processorName()
{
    int registers[4]{};
    __cpuid(registers, static_cast<int>(0x80000000));
    if (static_cast<unsigned>(registers[0]) < 0x80000004) return L"Procesor";
    std::array<char, 49> name{};
    for (unsigned i = 0; i < 3; ++i) {
        __cpuid(registers, static_cast<int>(0x80000002 + i));
        std::memcpy(name.data() + i * 16, registers, 16);
    }
    const std::string text(name.data());
    return {text.begin(), text.end()};
}

BOOL CALLBACK pageFiles(void* context, PENUM_PAGE_FILE_INFORMATION info, LPCWSTR)
{
    auto& memory = *static_cast<SystemMemory*>(context);
    SYSTEM_INFO system{}; GetSystemInfo(&system);
    const auto page = static_cast<std::uint64_t>(system.dwPageSize);
    memory.pageFileBytes += info->TotalSize * page;
    memory.pageFileUsedBytes += info->TotalInUse * page;
    memory.pageFilePeakBytes += info->PeakUsage * page;
    return TRUE;
}

}

ProcessCollector::ProcessCollector()
{
    boot = queryBootIdentity(queryProcesses);
    SYSTEM_TIMEOFDAY_INFORMATION time{};
    const auto status = NtQuerySystemInformation(SystemTimeOfDayInformation, &time, sizeof(time), nullptr);
    if (NT_SUCCESS(status) && time.BootTime.QuadPart > 0) bootTime = static_cast<std::uint64_t>(time.BootTime.QuadPart);
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
    result.boot = boot;
    result.bootTime = bootTime;
    static const auto memoryFacts = memoryHardware(), cpuFacts = processorHardware(), boardFacts = boardHardware();
    result.memoryFacts = memoryFacts; result.cpuFacts = cpuFacts; result.boardFacts = boardFacts;
    result.measuredAt = std::chrono::steady_clock::now();
    result.wallTime = std::chrono::system_clock::now();
    result.logicalProcessors = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
    static const auto model = processorName();
    result.cpuModel = model;
    if (!result.logicalProcessors)
        throw std::runtime_error("No active logical processors");
    result.processes = parseProcesses({static_cast<const std::byte*>(buffer), used}, boot.id);
    const auto parsedAt = std::chrono::steady_clock::now();
    result.queryTime = result.measuredAt - began;
    result.parseTime = parsedAt - result.measuredAt;

    std::vector<ProcessorGroup> groups;
    const auto groupCount = GetActiveProcessorGroupCount();
    for (USHORT number = 0; number < groupCount; ++number)
        groups.push_back({number, GetActiveProcessorCount(number)});
    const auto times = queryProcessorTimes(groups, queryGroup);
    result.processorState = times.state;
    result.systemIdle100ns = times.idle100ns;
    result.systemKernel100ns = times.kernel100ns;
    result.systemUser100ns = times.user100ns;
    result.processors = times.processors;

    PERFORMANCE_INFORMATION memory{sizeof(PERFORMANCE_INFORMATION)};
    if (GetPerformanceInfo(&memory, sizeof(memory))) {
        const auto page = static_cast<std::uint64_t>(memory.PageSize);
        result.memory = {memory.PhysicalTotal * page, memory.PhysicalAvailable * page, memory.CommitTotal * page,
            memory.CommitLimit * page, memory.SystemCache * page, memory.KernelPaged * page,
            memory.KernelNonpaged * page, SampleState::valid};
    }
    if (EnumPageFilesW(pageFiles, &result.memory)) result.memory.pageFileState = SampleState::valid;
    static const auto installed = [] { ULONGLONG kilobytes = 0; GetPhysicallyInstalledSystemMemory(&kilobytes); return kilobytes * 1024; }();
    result.memory.installedBytes = installed;
    result.collectionTime = std::chrono::steady_clock::now() - began;
    result.systemTime = std::chrono::steady_clock::now() - parsedAt;
    return result;
}

}
