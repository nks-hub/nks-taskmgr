#pragma once
#include "process_actions.hpp"
#include <array>

namespace nks::actions {
using CpuSetsHash = std::array<std::byte, 32>;
struct CpuSet {
    std::uint32_t id{}, group{}, logicalProcessor{}, core{}, numa{}, lastLevelCache{}, efficiency{}, flags{};
};
struct CpuSetsSnapshot {
    CpuSetsHash baseline{};
    std::vector<CpuSet> cpus;
    std::vector<std::uint32_t> defaults;
    bool valid{};
};
struct CpuSetsResult { Result result; CpuSetsSnapshot snapshot; };
void processCpuSets(const Context& context, Command command, const CpuSetsHash& baseline,
    std::span<const std::uint32_t> selected, CpuSetsResult& result);
void processHeldCpuSets(const Context& context, Command command, const CpuSetsHash& baseline,
    std::span<const std::uint32_t> selected, HANDLE process, CpuSetsResult& result);
}
