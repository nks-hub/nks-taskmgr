#pragma once
#include "protocol.hpp"

namespace nks::broker {
inline bool stdioIo(HANDLE pipe, std::span<std::byte> bytes, bool writing)
{
    while (!bytes.empty()) {
        DWORD count = 0;
        const BOOL done = writing ? WriteFile(pipe, bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr)
            : ReadFile(pipe, bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr);
        if (!done || !count || count > bytes.size()) return false;
        bytes = bytes.subspan(count);
    }
    return true;
}
inline bool stdioWrite(HANDLE pipe, std::span<const std::byte> bytes)
{
    return stdioIo(pipe, {const_cast<std::byte*>(bytes.data()), bytes.size()}, true);
}
inline bool decodeTargets(const actions::wire::Header& header, std::span<const std::byte> payload, std::vector<actions::Result>& results)
{
    using namespace actions;
    if (!wire::headerValid(header) || payload.size() != read32(header, 8)) return false;
    const auto count = read32(header, 12); const auto command = static_cast<Command>(read(header, 6, 2));
    results.resize(count);
    for (std::size_t i = 0; i < count; ++i) {
        const auto row = payload.subspan(i * targetBytes, targetBytes); const auto kind = read32(row, 20);
        auto& target = results[i].target;
        target.key = {{read64(row, 0), read64(row, 8)}, read32(row, 16), static_cast<GenerationKind>(kind), read64(row, 24)};
        target.created100ns = read64(row, 32); target.window = read64(row, 40);
        if (target.key.boot.empty() || kind < 1 || kind > 2 || !target.key.generation || !target.created100ns
            || (kind == 1 && target.key.generation != target.created100ns)
            || (command != Command::closeWindows && target.window) || target.window == 0xffff || target.window == UINT64_MAX) return false;
        for (std::size_t j = 0; j < i; ++j) if (results[j].target.key.pid == target.key.pid) return false;
    }
    return true;
}
int serve(const std::wstring& name, DWORD expectedClient);
int bridge();
}
