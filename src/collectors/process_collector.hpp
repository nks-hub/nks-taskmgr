#pragma once

#include "core/process.hpp"
#include <windows.h>

namespace nks {

class ProcessCollector {
public:
    ProcessCollector();
    ~ProcessCollector();
    ProcessCollector(const ProcessCollector&) = delete;
    ProcessCollector& operator=(const ProcessCollector&) = delete;
    ProcessSnapshot collect();

private:
    void* buffer{};
    ULONG capacity{};
    std::uint64_t bootTime{};
    BootIdentity boot;
};

}
