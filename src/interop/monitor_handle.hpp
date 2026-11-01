#pragma once
#include "monitor_api.h"
#include "core/monitor.hpp"

#include <mutex>
#include <string>

struct NksMonitor {
    nks::Monitor monitor;
    std::mutex readMutex;
    std::wstring processError, resourceError;
};
