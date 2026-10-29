#include <phnt_windows.h>
#include <phnt.h>
#include "collectors/memory_query.hpp"

#include <iostream>
#include <limits>
#include <stdexcept>
#include <vector>

namespace {

void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }

enum class Failure { none, denied, truncated, shortVersion, zeroPage, overflow };
Failure failure{};
ULONG failedClass{}, calls{};
std::vector<ULONG> requested;

LONG NTAPI query(ULONG informationClass, void* output, ULONG capacity, ULONG* returned)
{
    ++calls; requested.push_back(informationClass);
    if (informationClass == failedClass && failure == Failure::denied) return STATUS_ACCESS_DENIED;
    *returned = capacity;
    if (informationClass == SystemBasicInformation) {
        require(capacity == sizeof(SYSTEM_BASIC_INFORMATION), "Unexpected basic information size");
        auto& basic = *static_cast<SYSTEM_BASIC_INFORMATION*>(output);
        basic.PageSize = failure == Failure::zeroPage ? 0 : 8192;
        basic.NumberOfPhysicalPages = 1'000'000;
    } else if (informationClass == SystemPerformanceInformation) {
        require(capacity == sizeof(SYSTEM_PERFORMANCE_INFORMATION), "Unexpected performance information size");
        auto& performance = *static_cast<SYSTEM_PERFORMANCE_INFORMATION*>(output);
        performance.AvailablePages = 200'000; performance.CommittedPages = 1'200'000;
        performance.CommitLimit = 2'000'000; performance.PagedPoolPages = 3000; performance.NonPagedPoolPages = 4000;
        if (failure == Failure::shortVersion)
            *returned = offsetof(SYSTEM_PERFORMANCE_INFORMATION, NonPagedPoolPages) + sizeof(performance.NonPagedPoolPages);
    } else if (informationClass == SystemFileCacheInformation) {
        require(capacity == sizeof(SYSTEM_FILECACHE_INFORMATION), "Unexpected file cache information size");
        auto& cache = *static_cast<SYSTEM_FILECACHE_INFORMATION*>(output);
        cache.CurrentSize = 123;
        cache.CurrentSizeIncludingTransitionInPages = failure == Failure::overflow ? std::numeric_limits<SIZE_T>::max() : 150'000;
    } else throw std::runtime_error("Memory statistics queried an unrelated information class");
    if (informationClass == failedClass && failure == Failure::truncated) *returned = 1;
    return STATUS_SUCCESS;
}

void reset(Failure mode = Failure::none, ULONG informationClass = 0)
{
    failure = mode; failedClass = informationClass; calls = 0; requested.clear();
}

}

int main()
{
    try {
        reset();
        const auto memory = nks::querySystemMemory(query);
        require(memory.state == nks::SampleState::valid, "Complete memory statistics are unavailable");
        require(memory.physicalBytes == 8'192'000'000ull && memory.availableBytes == 1'638'400'000ull,
            "Physical memory pages were not converted to 64-bit bytes");
        require(memory.committedBytes == 9'830'400'000ull && memory.commitLimitBytes == 16'384'000'000ull,
            "System commit was confused with pagefile capacity");
        require(memory.systemCacheBytes == 1'228'800'000ull, "Cache omitted transition pages or used a byte count as pages");
        require(memory.pagedPoolBytes == 24'576'000 && memory.nonpagedPoolBytes == 32'768'000, "Kernel pools are incorrect");
        require(calls == 3 && requested == std::vector<ULONG>{SystemBasicInformation, SystemPerformanceInformation, SystemFileCacheInformation},
            "Memory statistics enumerated processes or made additional queries");
        require(memory.pageFileState == nks::SampleState::unavailable, "Memory statistics invented pagefile usage");
        reset(Failure::shortVersion);
        require(nks::querySystemMemory(query).state == nks::SampleState::valid, "An older performance structure with all required fields was rejected");
        for (const auto informationClass : {SystemBasicInformation, SystemPerformanceInformation, SystemFileCacheInformation}) {
            for (const auto mode : {Failure::denied, Failure::truncated}) {
                reset(mode, informationClass);
                const auto missing = nks::querySystemMemory(query);
                require(missing.state != nks::SampleState::valid && missing.physicalBytes == 0 && missing.systemCacheBytes == 0,
                    "Failed or truncated statistics exposed a valid partial result");
            }
        }
        for (const auto mode : {Failure::zeroPage, Failure::overflow}) {
            reset(mode);
            require(nks::querySystemMemory(query).state != nks::SampleState::valid, "Invalid page size or byte overflow was accepted");
        }
        require(nks::querySystemMemory(nullptr).state != nks::SampleState::valid, "Missing query returned valid memory statistics");
        std::cout << "PASS memory statistics, units, query scope and unavailable results\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
