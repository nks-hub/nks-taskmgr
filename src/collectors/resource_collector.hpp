#pragma once

#include "pdh_counters.hpp"
#include "cpu_sensors.hpp"
#include <map>

namespace nks {

class ResourceCollector {
public:
    ResourceSnapshot collect(bool cpuSensors = false);
private:
    void inventory();
    void networks(ResourceSnapshot& snapshot);
    struct NetworkBefore {
        std::uint64_t received{}, sent{};
        bool connected{};
        std::chrono::steady_clock::time_point measuredAt;
    };
    PdhCounters counters;
    CpuSensors cpuTemperature;
    std::vector<ResourceRecord> devices;
    std::map<std::wstring, std::uint32_t> diskNumbers;
    std::map<std::wstring, std::vector<HardwareFact>> diskHealthFacts;
    std::chrono::steady_clock::time_point diskHealthAt{};
    std::map<std::wstring, std::wstring> gpuLuids;
    std::map<std::wstring, LUID> gpuLuidValues;
    std::map<std::uint64_t, NetworkBefore> previousNetworks;
    std::vector<ResourceRecord> lastNetworks;
    std::vector<HardwareFact> upsFacts;
    std::chrono::steady_clock::time_point upsAt{};
    std::chrono::steady_clock::time_point inventoryAt{};
    std::chrono::steady_clock::time_point telemetryAt{};
    bool disksFresh{}, gpusFresh{};
};

}
