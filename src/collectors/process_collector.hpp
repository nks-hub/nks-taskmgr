#pragma once

#include "core/process.hpp"
#include "process_identity.hpp"
#include <windows.h>

namespace nks {

class ProcessCollector {
public:
    explicit ProcessCollector(ProcessIdentityPreference preference = ProcessIdentityPreference::creationTime);
    ~ProcessCollector();
    ProcessCollector(const ProcessCollector&) = delete;
    ProcessCollector& operator=(const ProcessCollector&) = delete;
    /// Without processes only processor times and memory are read; the snapshot's process list stays empty.
    ProcessSnapshot collect(bool includeProcesses = true);

private:
    void* buffer{};
    ULONG capacity{};
    void* basicBuffer{};
    ULONG basicCapacity{};
    bool sequenceChecked{}, sequenceEnabled{};
    std::uint64_t bootTime{};
    BootIdentity boot;
    std::vector<ProcessRecord> collectProcesses(std::chrono::steady_clock::time_point& measuredAt,
        std::chrono::system_clock::time_point& wallTime);
};

}
