#include <phnt_windows.h>
#include <phnt.h>
#include "processor_query.hpp"

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

}
