#pragma once

#include "process.hpp"
#include <optional>
#include <unordered_map>

namespace nks {

class ProcessSampler {
public:
    void apply(ProcessSnapshot& snapshot);
    void reset() noexcept { previous.reset(); processIndex.clear(); }

private:
    struct IdentityHash {
        std::size_t operator()(const ProcessIdentity& identity) const noexcept;
    };
    using ProcessIndex = std::unordered_map<ProcessIdentity, std::size_t, IdentityHash>;
    struct ProcessBaseline {
        ProcessIdentity identity;
        std::uint64_t kernel100ns{}, user100ns{}, readIoBytes{}, writeIoBytes{};
    };
    struct ProcessorBaseline {
        std::uint16_t group{}, index{};
        std::uint64_t kernel100ns{}, user100ns{}, idle100ns{};
    };
    struct Baseline {
        std::chrono::steady_clock::time_point measuredAt;
        std::uint32_t logicalProcessors{};
        std::uint64_t systemKernel100ns{}, systemUser100ns{}, systemIdle100ns{};
        SampleState processorState{};
        std::int64_t systemCpuObservedAtNs{}, logicalCpuObservedAtNs{}, systemMemoryObservedAtNs{};
        std::vector<ProcessBaseline> processes;
        std::vector<ProcessorBaseline> processors;
    };
    void remember(const ProcessSnapshot& snapshot);
    std::optional<Baseline> previous;
    ProcessIndex processIndex;
};

}
