#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <span>
#include <stdexcept>
#include <string>
#include <vector>

namespace nks::update {
constexpr std::uint32_t magic = 0x3150554e;
constexpr std::size_t headerBytes = 128, resultBytes = 40, packageBytes = 144, planBytes = 304;
constexpr std::size_t maxPayload = 65536, maxPackages = 8, maxPlans = 16, maxJournalFiles = 80;
constexpr std::uint64_t maxPackageSize = 512ull * 1024 * 1024, maxCacheSize = 2ull * 1024 * 1024 * 1024;
enum class Command : std::uint16_t { list = 1, cache, plan, apply, rollback, commit };
enum class Frame : std::uint32_t { request, result, ready, accepted, rejected };
enum class Status : std::uint32_t { ok, trustMissing, rejected, invalidRequest, cacheFull, conflict, notFound, io, guiAlive, installerFailed, verificationFailed, unknown, busy };
enum class State : std::uint32_t { none, cached, planned, committed, waitingGui, installing, verified, rejected, guiAlive, installerFailed, verificationFailed, unknown };
enum class Action : std::uint32_t { update = 1, rollback };
using Header = std::array<std::byte, headerBytes>;
using Hash = std::array<std::byte, 32>;
using PlanId = std::array<std::byte, 16>;
inline std::uint64_t read(std::span<const std::byte> data, std::size_t at, std::size_t count)
{
    if (at > data.size() || count > data.size() - at || count > 8) throw std::runtime_error("Invalid update frame bounds");
    std::uint64_t result = 0;
    for (std::size_t i = 0; i < count; ++i) result |= static_cast<std::uint64_t>(std::to_integer<unsigned>(data[at + i])) << (i * 8);
    return result;
}
inline void write(std::span<std::byte> data, std::size_t at, std::uint64_t value, std::size_t count)
{
    if (at > data.size() || count > data.size() - at || count > 8) throw std::runtime_error("Invalid update frame bounds");
    for (std::size_t i = 0; i < count; ++i) data[at + i] = static_cast<std::byte>((value >> (i * 8)) & 255);
}
inline std::wstring text(std::span<const std::byte> data, std::size_t& at, std::size_t chars)
{
    if (chars > 32767 || at > data.size() || chars * 2 > data.size() - at) throw std::runtime_error("Invalid update text bounds");
    std::wstring result; result.reserve(chars);
    for (std::size_t i = 0; i < chars; ++i) {
        const auto ch = static_cast<wchar_t>(read(data, at, 2)); at += 2;
        if (!ch || ch < L' ') throw std::runtime_error("Invalid update text");
        result += ch;
    }
    return result;
}
inline void appendText(std::vector<std::byte>& data, const std::wstring& value)
{
    if (value.size() > 32767 || data.size() + value.size() * 2 > maxPayload) throw std::runtime_error("Update payload limit");
    const auto at = data.size(); data.resize(at + value.size() * 2);
    for (std::size_t i = 0; i < value.size(); ++i) write(data, at + i * 2, value[i], 2);
}
inline bool empty(const Hash& hash) { for (auto value : hash) if (value != std::byte{}) return false; return true; }
std::wstring hex(std::span<const std::byte> bytes);
}
