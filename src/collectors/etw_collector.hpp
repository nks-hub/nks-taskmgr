#pragma once

#include "core/process_events.hpp"
#include <cstdint>
#include <memory>
#include <string>
#include <string_view>

namespace nks {

/// Session names carry the owner's PID and creation time, so a later run can stop sessions left behind by a process that is gone.
std::wstring etwSessionName(std::uint32_t pid, std::uint64_t created, std::wstring_view unique);
/// Reads the owner from a name made by etwSessionName; false for any other name, including the older format without an owner.
bool etwSessionOwner(std::wstring_view name, std::uint32_t& pid, std::uint64_t& created) noexcept;

class EtwCollector {
public:
    explicit EtwCollector(std::shared_ptr<ProcessEventStore> events);
    ~EtwCollector();
    EtwCollector(const EtwCollector&) = delete;
    EtwCollector& operator=(const EtwCollector&) = delete;
    void start();
    void stop() noexcept;
private:
    class Run;
    std::shared_ptr<ProcessEventStore> events;
    std::shared_ptr<Run> run;
    std::mutex mutex;
};

}
