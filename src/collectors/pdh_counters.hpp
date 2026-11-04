#pragma once

#include "core/resource.hpp"
#include <windows.h>
#include <pdh.h>
#include <memory>

namespace nks {

using PdhArrayRead = PDH_STATUS (WINAPI*)(PDH_HCOUNTER, DWORD, LPDWORD, LPDWORD, PPDH_FMT_COUNTERVALUE_ITEM_W);
// Only PDH_CSTATUS_VALID_DATA/NEW_DATA with a finite non-negative value is valid; a failed read yields no instances.
std::vector<NamedCounter> readCounterArray(PDH_HCOUNTER counter, std::unique_ptr<std::byte[]>& scratch,
    DWORD& scratchBytes, PdhArrayRead read = PdhGetFormattedCounterArrayW);

class PdhCounters {
public:
    PdhCounters();
    ~PdhCounters();
    PdhCounters(const PdhCounters&) = delete;
    PdhCounters& operator=(const PdhCounters&) = delete;
    bool collect();
    std::vector<NamedCounter> read(std::size_t index) const;
    MeasurementMetadata measurement(std::size_t index) const;
    bool available(std::size_t index) const { return counters.at(index) != nullptr; }
private:
    void retryMissing();
    PDH_HQUERY query{};
    std::array<PDH_HCOUNTER, 12> counters{};
    std::chrono::steady_clock::time_point retryAt{};
    std::int64_t observedAtNs{}, previousObservedAtNs{};
    // Reads are serialized by the owning resource collector thread.
    mutable std::unique_ptr<std::byte[]> scratch;
    mutable DWORD scratchBytes{};
};

}
