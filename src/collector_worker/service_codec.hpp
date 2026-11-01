#pragma once

#include "wire.hpp"
#include "core/service.hpp"

namespace nks::collector {

std::vector<std::byte> encode(const ServiceSnapshot& snapshot);
std::vector<std::byte> encode(const ServiceDetail& detail);
ServiceSnapshot decodeServices(std::span<const std::byte> payload);
ServiceDetail decodeServiceDetail(std::span<const std::byte> payload);

}
