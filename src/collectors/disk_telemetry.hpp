#pragma once
#include "core/hardware.hpp"

namespace nks {
std::vector<HardwareFact> diskHardware(const std::wstring& path);
std::vector<HardwareFact> diskHealth(const std::wstring& path);
std::vector<HardwareFact> decodeNvmeHealth(std::span<const std::byte> descriptor);
}
