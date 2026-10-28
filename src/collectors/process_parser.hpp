#pragma once

#include "core/process.hpp"
#include <cstddef>
#include <span>

namespace nks {

std::vector<ProcessRecord> parseProcesses(std::span<const std::byte> buffer, std::uint64_t bootTime);

}
