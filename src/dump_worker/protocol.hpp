#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <span>
#include <stdexcept>

namespace nks::dump {
constexpr std::uint32_t magic = 0x31444d4e;
constexpr std::uint16_t version = 1;
constexpr std::size_t requestBytes = 112, responseBytes = 168, maximumPathChars = 32760;
constexpr std::uint64_t maximumFileBytes = 32ull * 1024 * 1024 * 1024;
enum class Mode : std::uint16_t { mini, full };
enum class State : std::uint32_t { captured, denied, unsupported, targetChanged, unavailable, limitExceeded, error, canceled, invalidRequest };
enum class Architecture : std::uint32_t { unknown, x86, x64 };

inline std::uint64_t read(std::span<const std::byte> bytes, std::size_t at, std::size_t size)
{
    if (at > bytes.size() || size > bytes.size() - at || size > 8) throw std::invalid_argument("Invalid dump frame");
    std::uint64_t value = 0;
    for (std::size_t i = 0; i < size; ++i) value |= static_cast<std::uint64_t>(std::to_integer<unsigned>(bytes[at + i])) << (i * 8);
    return value;
}
inline std::uint32_t read32(std::span<const std::byte> bytes, std::size_t at) { return static_cast<std::uint32_t>(read(bytes, at, 4)); }
inline std::uint64_t read64(std::span<const std::byte> bytes, std::size_t at) { return read(bytes, at, 8); }
inline void write(std::span<std::byte> bytes, std::size_t at, std::uint64_t value, std::size_t size)
{
    if (at > bytes.size() || size > bytes.size() - at || size > 8) throw std::invalid_argument("Invalid dump response");
    for (std::size_t i = 0; i < size; ++i) bytes[at + i] = static_cast<std::byte>((value >> (i * 8)) & 0xff);
}
}
