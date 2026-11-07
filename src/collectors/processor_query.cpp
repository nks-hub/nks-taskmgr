#include <phnt_windows.h>
#include <phnt.h>
#include "processor_query.hpp"
#include <pdh.h>
#include <pdhmsg.h>

#include <cmath>
#include <limits>
#include <vector>

namespace nks {

ProcessorTimes queryProcessorTimes(std::span<const ProcessorGroup> groups, ProcessorGroupQuery query)
{
    ProcessorTimes result;
    if (!query || groups.empty())
        return result;
    std::vector<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION> entries;
    for (const auto& group : groups) {
        if (group.activeProcessors == 0 || group.activeProcessors > MAXIMUM_PROC_PER_GROUP)
            return {};
        entries.resize(group.activeProcessors);
        const auto bytes = static_cast<ULONG>(entries.size() * sizeof(entries[0]));
        ULONG returned = 0;
        const auto status = query(group.number, entries.data(), bytes, &returned);
        if (!NT_SUCCESS(status) || returned != bytes)
            return {};
        for (std::size_t index = 0; index < entries.size(); ++index) {
            const auto& entry = entries[index];
            if (entry.KernelTime.QuadPart < 0 || entry.UserTime.QuadPart < 0 || entry.IdleTime.QuadPart < 0)
                return {0, 0, 0, SampleState::error};
            const auto kernel = static_cast<std::uint64_t>(entry.KernelTime.QuadPart);
            const auto user = static_cast<std::uint64_t>(entry.UserTime.QuadPart);
            const auto idle = static_cast<std::uint64_t>(entry.IdleTime.QuadPart);
            constexpr auto limit = std::numeric_limits<std::uint64_t>::max();
            if (kernel > limit - result.kernel100ns || user > limit - result.user100ns || idle > limit - result.idle100ns)
                return {0, 0, 0, SampleState::error};
            result.kernel100ns += kernel;
            result.user100ns += user;
            result.idle100ns += idle;
            result.processors.push_back({group.number, static_cast<std::uint16_t>(index), kernel, user, idle});
        }
    }
    result.state = SampleState::valid;
    return result;
}

std::vector<HardwareFact> processorSpeedFacts(double nominalMhz, double performancePercent)
{
    if (!std::isfinite(nominalMhz) || nominalMhz < 1) return {};
    std::vector<HardwareFact> result;
    if (std::isfinite(performancePercent) && performancePercent > 0)
        result.push_back({L"Rychlost", std::to_wstring(std::lround(nominalMhz * performancePercent / 100)) + L" MHz"});
    result.push_back({L"Základní rychlost", std::to_wstring(std::lround(nominalMhz)) + L" MHz"});
    return result;
}

ProcessorSpeed::ProcessorSpeed()
{
    PDH_HQUERY handle{};
    if (PdhOpenQueryW(nullptr, 0, &handle) != ERROR_SUCCESS) return;
    query = handle;
    PDH_HCOUNTER counter{};
    if (PdhAddEnglishCounterW(handle, L"\\Processor Information(_Total)\\% Processor Performance", 0, &counter) == ERROR_SUCCESS) performance = counter;
    if (PdhAddEnglishCounterW(handle, L"\\Processor Information(_Total)\\Processor Frequency", 0, &counter) == ERROR_SUCCESS) nominal = counter;
    PdhCollectQueryData(handle);
}

ProcessorSpeed::~ProcessorSpeed() { if (query) PdhCloseQuery(static_cast<PDH_HQUERY>(query)); }

std::vector<HardwareFact> ProcessorSpeed::collect()
{
    if (!query || !nominal || PdhCollectQueryData(static_cast<PDH_HQUERY>(query)) != ERROR_SUCCESS) return {};
    const auto read = [](void* counter) {
        PDH_FMT_COUNTERVALUE value{};
        if (!counter || PdhGetFormattedCounterValue(static_cast<PDH_HCOUNTER>(counter), PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, nullptr, &value) != ERROR_SUCCESS
            || (value.CStatus != PDH_CSTATUS_VALID_DATA && value.CStatus != PDH_CSTATUS_NEW_DATA)) return std::numeric_limits<double>::quiet_NaN();
        return value.doubleValue;
    };
    return processorSpeedFacts(read(nominal), read(performance));
}

}
