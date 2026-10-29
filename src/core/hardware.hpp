#pragma once
#include <cstddef>
#include <span>
#include <string>
#include <vector>

namespace nks {
struct HardwareFact { std::wstring name, value; };
std::vector<HardwareFact> parseMemoryHardware(std::span<const std::byte> table);
std::vector<HardwareFact> memoryHardware();
std::vector<HardwareFact> processorHardware();
std::vector<HardwareFact> boardHardware();
std::vector<HardwareFact> upsHardware();
std::wstring hardwareRate(unsigned long long bits);
}
