#pragma once

#include "pdh_counters.hpp"
#include <map>

namespace nks {

class ResourceCollector {
public:
    ResourceSnapshot collect();
private:
    void inventory();
    void networks(ResourceSnapshot& snapshot);
    struct NetworkBefore {
        std::uint64_t received{}, sent{};
        bool connected{};
        std::chrono::steady_clock::time_point measuredAt;
    };
    PdhCounters counters;
    std::vector<ResourceRecord> devices;
    std::map<std::wstring, std::uint32_t> diskNumbers;
    std::map<std::wstring, std::wstring> gpuLuids;
    std::map<std::uint64_t, NetworkBefore> previousNetworks;
    std::vector<ResourceRecord> lastNetworks;
    std::chrono::steady_clock::time_point inventoryAt{};
    bool disksFresh{}, gpusFresh{};
};

}
