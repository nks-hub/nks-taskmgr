#include "process_details.hpp"
#include <algorithm>
#include <limits>

namespace nks {

void readProcessMemory(HANDLE process, ProcessDetailsResult& result)
{
    SYSTEM_INFO system{};
    GetNativeSystemInfo(&system);
    const auto maximum = reinterpret_cast<std::uintptr_t>(system.lpMaximumApplicationAddress);
    if (maximum == std::numeric_limits<std::uintptr_t>::max()) {
        result.state = details::ResultState::error; result.win32Error = ERROR_INVALID_DATA; return;
    }
    const auto end = result.architecture == details::Architecture::x86
        ? std::uint64_t{1} << 32 : static_cast<std::uint64_t>(maximum) + 1;
    const auto began = GetTickCount64();
    std::uint64_t cursor = 0;
    while (cursor < end) {
        if (result.memory.size() == details::maximumMemoryRegions || GetTickCount64() - began > 3000) {
            result.state = details::ResultState::limitExceeded; result.win32Error = ERROR_INSUFFICIENT_BUFFER; return;
        }
        MEMORY_BASIC_INFORMATION native{};
        const auto used = VirtualQueryEx(process, reinterpret_cast<const void*>(static_cast<std::uintptr_t>(cursor)), &native, sizeof(native));
        if (!used) {
            result.win32Error = GetLastError();
            result.state = result.win32Error == ERROR_ACCESS_DENIED ? details::ResultState::accessDenied : details::ResultState::unavailable;
            return;
        }
        const auto base = static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(native.BaseAddress));
        const auto size = static_cast<std::uint64_t>(native.RegionSize);
        if (used != sizeof(native) || base != cursor || !size || size > std::numeric_limits<std::uint64_t>::max() - base
            || (native.State != MEM_FREE && native.State != MEM_RESERVE && native.State != MEM_COMMIT)) {
            result.state = details::ResultState::error; result.win32Error = ERROR_INVALID_DATA; return;
        }
        details::MemoryRegion row;
        row.base = base; row.regionSize = std::min(size, end - base); row.state = native.State;
        // VirtualQueryEx leaves these fields undefined for free or reserved regions.
        if (native.State != MEM_FREE) {
            row.allocationBase = reinterpret_cast<std::uintptr_t>(native.AllocationBase);
            row.allocationProtect = native.AllocationProtect; row.type = native.Type;
            if (native.State == MEM_COMMIT) row.protect = native.Protect;
        }
        result.memory.push_back(row);
        cursor = base + row.regionSize;
    }
    result.state = details::ResultState::ok;
}

}
