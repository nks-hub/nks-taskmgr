#pragma once
#include "monitor_api.h"
#include "core/monitor.hpp"

#include <mutex>
#include <string>

struct NksMonitor {
    nks::Monitor monitor;
    std::mutex readMutex;
    std::wstring processError, resourceError;
    std::shared_ptr<const nks::ProcessSnapshot> previousProcesses;
    std::uint64_t processRevision{};
};
