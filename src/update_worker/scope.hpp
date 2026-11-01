#pragma once
#include "core/update_image.hpp"

namespace nks::update {
struct Scope {
    InstallMode mode{};
    FILE_ID_INFO id{};
    std::wstring path, detail;
    broker::Handle root;
};
Scope classifyScope(const broker::Peer& gui);
void requireScope(const Scope& scope);
bool sameRoot(const FILE_ID_INFO& left, const FILE_ID_INFO& right) noexcept;
}
