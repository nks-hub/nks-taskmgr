#pragma once

#include "core/process_events.hpp"
#include <memory>

namespace nks {

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
