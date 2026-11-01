#pragma once

#include "wire.hpp"
#include "core/process.hpp"
#include "core/resource.hpp"

namespace nks::collector {

std::vector<std::byte> encode(const ProcessSnapshot& snapshot);
std::vector<std::byte> encode(const ResourceSnapshot& snapshot);
ProcessSnapshot decodeProcesses(std::span<const std::byte> payload);
ResourceSnapshot decodeResources(std::span<const std::byte> payload);

}
