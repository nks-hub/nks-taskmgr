#include <phnt_windows.h>
#include <phnt.h>
#include <psapi.h>
#include "process_collector.hpp"
#include "process_parser.hpp"
#include "process_query.h"
#include "processor_query.hpp"
#include "memory_query.hpp"
#include "boot_identity.hpp"

#include <stdexcept>
#include <vector>
#include <intrin.h>
#include <array>
#include <cstring>
#include <limits>

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

bool unsupportedProcessQuery(LONG status)
{
    return status == STATUS_INVALID_INFO_CLASS || status == STATUS_NOT_IMPLEMENTED || status == STATUS_NOT_SUPPORTED;
}

void requireProcessQuery(LONG status, const char* stage, ULONG remaining)
{
    if (!NT_SUCCESS(status))
        throw std::runtime_error("NtQuerySystemInformation process query failed: " + std::to_string(static_cast<unsigned long>(status))
            + "; stage=" + stage + "; remaining=" + std::to_string(remaining));
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

ProcessCollector::ProcessCollector(ProcessIdentityPreference preference)
{
    boot = queryBootIdentity(queryProcesses);
    sequenceChecked = preference == ProcessIdentityPreference::creationTime || boot.state != SampleState::valid || boot.id.empty();
    SYSTEM_TIMEOFDAY_INFORMATION time{};
    const auto status = NtQuerySystemInformation(SystemTimeOfDayInformation, &time, sizeof(time), nullptr);
    if (NT_SUCCESS(status) && time.BootTime.QuadPart > 0) bootTime = static_cast<std::uint64_t>(time.BootTime.QuadPart);
}

ProcessCollector::~ProcessCollector() { NksFreeProcessBuffer(buffer); NksFreeProcessBuffer(basicBuffer); }

std::vector<ProcessRecord> ProcessCollector::collectProcesses(std::chrono::steady_clock::time_point& measuredAt,
    std::chrono::system_clock::time_point& wallTime)
{
    NksProcessQueryBudget budget{NKS_PROCESS_QUERY_ATTEMPTS, capacity + basicCapacity};
    std::vector<BasicProcessIdentity> before;
    if (!sequenceChecked || sequenceEnabled) {
        ULONG used = 0;
        const auto status = NksQueryProcessBufferBudgeted(queryProcesses, SystemBasicProcessInformation,
            &basicBuffer, &basicCapacity, &used, &budget);
        if (!sequenceChecked && unsupportedProcessQuery(status)) {
            sequenceChecked = true; sequenceEnabled = false;
            NksFreeProcessBuffer(basicBuffer); basicBuffer = nullptr;
            budget.allocated_bytes -= basicCapacity; basicCapacity = 0;
        }
        else {
            requireProcessQuery(status, "identity before", budget.remaining_attempts);
            before = parseBasicProcesses({static_cast<const std::byte*>(basicBuffer), used});
            sequenceChecked = sequenceEnabled = true;
        }
    }
    ULONG used = 0;
    const auto processStatus = NksQueryProcessBufferBudgeted(queryProcesses, SystemProcessInformation, &buffer, &capacity, &used, &budget);
    requireProcessQuery(processStatus, "process counters", budget.remaining_attempts);
    measuredAt = std::chrono::steady_clock::now();
    wallTime = std::chrono::system_clock::now();
    auto processes = parseProcesses({static_cast<const std::byte*>(buffer), used}, boot.id);
    if (!sequenceEnabled) return processes;
    const auto afterStatus = NksQueryProcessBufferBudgeted(queryProcesses, SystemBasicProcessInformation,
        &basicBuffer, &basicCapacity, &used, &budget);
    requireProcessQuery(afterStatus, "identity after", budget.remaining_attempts);
    const auto after = parseBasicProcesses({static_cast<const std::byte*>(basicBuffer), used});
    if (!applySequenceIdentities(processes, before, after))
        throw std::runtime_error("Invalid process identity snapshot");
    return processes;
}

ProcessSnapshot ProcessCollector::collect()
{
    const auto began = std::chrono::steady_clock::now();
    ProcessSnapshot result;
    result.boot = boot;
    result.bootTime = bootTime;
    result.processes = collectProcesses(result.measuredAt, result.wallTime);
    result.measurements.processCpu = measurementMetadata(MeasurementUnit::percent, MeasurementNormalization::wholeMachine,
        MeasurementBasis::elapsedDelta, measurementTime(result.measuredAt));
    result.measurements.processIo = measurementMetadata(MeasurementUnit::bytesPerSecond, MeasurementNormalization::none,
        MeasurementBasis::elapsedDelta, measurementTime(result.measuredAt));
    result.measurements.processMemory = measurementMetadata(MeasurementUnit::bytes, MeasurementNormalization::none,
        MeasurementBasis::gauge, measurementTime(result.measuredAt));
    const auto parsedAt = std::chrono::steady_clock::now();
    static const auto memoryFacts = memoryHardware(), cpuFacts = processorHardware(), boardFacts = boardHardware();
    result.memoryFacts = memoryFacts; result.cpuFacts = cpuFacts; result.boardFacts = boardFacts;
    result.logicalProcessors = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
    result.measurements.processCpu.normalizationDivisor = result.logicalProcessors;
    static const auto model = processorName();
    result.cpuModel = model;
    if (!result.logicalProcessors)
        throw std::runtime_error("No active logical processors");
    result.queryTime = result.measuredAt - began;
    result.parseTime = parsedAt - result.measuredAt;

    std::vector<ProcessorGroup> groups;
    const auto groupCount = GetActiveProcessorGroupCount();
    for (USHORT number = 0; number < groupCount; ++number)
        groups.push_back({number, GetActiveProcessorCount(number)});
    const auto times = queryProcessorTimes(groups, queryGroup);
    const auto processorObserved = measurementTime(std::chrono::steady_clock::now());
    result.measurements.systemCpu = measurementMetadata(MeasurementUnit::percent, MeasurementNormalization::wholeMachine,
        MeasurementBasis::counterRatio, processorObserved);
    result.measurements.logicalCpu = measurementMetadata(MeasurementUnit::percent, MeasurementNormalization::logicalProcessor,
        MeasurementBasis::counterRatio, processorObserved);
    result.processorState = times.state;
    result.systemIdle100ns = times.idle100ns;
    result.systemKernel100ns = times.kernel100ns;
    result.systemUser100ns = times.user100ns;
    result.processors = times.processors;

    result.memory = querySystemMemory(queryProcesses);
    result.measurements.systemMemory = measurementMetadata(MeasurementUnit::bytes, MeasurementNormalization::none,
        MeasurementBasis::gauge, measurementTime(std::chrono::steady_clock::now()));
    if (EnumPageFilesW(pageFiles, &result.memory)) result.memory.pageFileState = SampleState::valid;
    static const auto installed = [] { ULONGLONG kilobytes = 0; GetPhysicallyInstalledSystemMemory(&kilobytes); return kilobytes * 1024; }();
    result.memory.installedBytes = installed;
    result.collectionTime = std::chrono::steady_clock::now() - began;
    result.systemTime = std::chrono::steady_clock::now() - parsedAt;
    return result;
}

}
