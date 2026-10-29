#include <phnt_windows.h>
#include <phnt.h>
#include "memory_query.hpp"

#include <array>
#include <limits>

namespace nks {
namespace {

template<class T>
bool read(NKS_SYSTEM_QUERY query, SYSTEM_INFORMATION_CLASS informationClass, T& value, ULONG minimumBytes)
{
    ULONG returned = 0;
    return NT_SUCCESS(query(informationClass, &value, sizeof(value), &returned))
        && returned >= minimumBytes && returned <= sizeof(value);
}

}

SystemMemory querySystemMemory(NKS_SYSTEM_QUERY query)
{
    if (!query) return {};
    SYSTEM_BASIC_INFORMATION basic{};
    SYSTEM_PERFORMANCE_INFORMATION performance{};
    SYSTEM_FILECACHE_INFORMATION cache{};
    if (!read(query, SystemBasicInformation, basic, sizeof(basic))
        || !read(query, SystemPerformanceInformation, performance,
            offsetof(SYSTEM_PERFORMANCE_INFORMATION, NonPagedPoolPages) + sizeof(performance.NonPagedPoolPages))
        || !read(query, SystemFileCacheInformation, cache,
            offsetof(SYSTEM_FILECACHE_INFORMATION, CurrentSizeIncludingTransitionInPages) + sizeof(cache.CurrentSizeIncludingTransitionInPages))
        || !basic.PageSize)
        return {};

    const std::array<std::uint64_t, 7> pages{basic.NumberOfPhysicalPages, performance.AvailablePages,
        performance.CommittedPages, performance.CommitLimit, cache.CurrentSizeIncludingTransitionInPages,
        performance.PagedPoolPages, performance.NonPagedPoolPages};
    std::array<std::uint64_t, 7> bytes{};
    for (std::size_t i = 0; i < pages.size(); ++i) {
        if (pages[i] > std::numeric_limits<std::uint64_t>::max() / basic.PageSize) return {};
        bytes[i] = pages[i] * basic.PageSize;
    }
    return {bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], SampleState::valid};
}

}
