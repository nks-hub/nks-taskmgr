#pragma once

#include "core/process.hpp"
#include "process_identity.hpp"
#include <cstddef>
#include <span>

namespace nks {

std::vector<ProcessRecord> parseProcesses(std::span<const std::byte> buffer, BootIdentifier boot);
std::vector<BasicProcessIdentity> parseBasicProcesses(std::span<const std::byte> buffer);
bool applySequenceIdentities(std::vector<ProcessRecord>& processes,
    std::span<const BasicProcessIdentity> before, std::span<const BasicProcessIdentity> after);

}
