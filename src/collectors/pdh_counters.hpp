#pragma once

#include "core/resource.hpp"
#include <windows.h>
#include <pdh.h>

namespace nks {

class PdhCounters {
public:
    PdhCounters();
    ~PdhCounters();
    PdhCounters(const PdhCounters&) = delete;
    PdhCounters& operator=(const PdhCounters&) = delete;
    bool collect();
    std::vector<NamedCounter> read(std::size_t index) const;
    bool available(std::size_t index) const { return counters.at(index) != nullptr; }
private:
    void retryMissing();
    PDH_HQUERY query{};
    std::array<PDH_HCOUNTER, 4> counters{};
    std::chrono::steady_clock::time_point retryAt{};
};

}
