#pragma once

#include <bit>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>
#include <stdexcept>
#include <string>
#include <type_traits>
#include <vector>

namespace nks::collector {

constexpr std::size_t maximumPayload = 64 * 1024 * 1024;
constexpr std::size_t headerBytes = 32;
constexpr std::uint32_t protocolVersion = 2;
enum class Kind : std::uint32_t { processes = 1, resources = 2, services = 3, serviceDetail = 4 };
struct Header {
    Kind kind{};
    std::uint32_t status{};
    std::uint64_t requestId{};
    std::uint32_t payloadBytes{};
};

class Writer {
public:
    template<class T> void number(T value) {
        static_assert(std::is_unsigned_v<T>);
        require(sizeof(T));
        for (std::size_t i = 0; i < sizeof(T); ++i) {
            bytes.push_back(static_cast<std::byte>(value & 0xff)); value >>= 8;
        }
    }
    void signedNumber(std::int64_t value) { number(std::bit_cast<std::uint64_t>(value)); }
    void real(double value) {
        if (!std::isfinite(value)) throw std::runtime_error("Non-finite collector value");
        number(std::bit_cast<std::uint64_t>(value));
    }
    void text(const std::wstring& value) {
        if (value.size() > 32767) throw std::length_error("Collector string exceeds limit");
        require(4 + value.size() * 2);
        number(static_cast<std::uint32_t>(value.size()));
        for (auto ch : value) number(static_cast<std::uint16_t>(ch));
    }
    void count(std::size_t value, std::size_t maximum) {
        if (value > maximum) throw std::length_error("Collector array exceeds limit");
        number(static_cast<std::uint32_t>(value));
    }
    std::vector<std::byte> bytes;
private:
    void require(std::size_t count) const {
        if (count > maximumPayload - bytes.size()) throw std::length_error("Collector response exceeds limit");
    }
};

class Reader {
public:
    explicit Reader(std::span<const std::byte> source) : bytes(source) {
        if (source.size() > maximumPayload) throw std::length_error("Collector response exceeds limit");
    }
    template<class T> T number() {
        static_assert(std::is_unsigned_v<T>);
        require(sizeof(T));
        std::uint64_t value{};
        for (std::size_t i = 0; i < sizeof(T); ++i)
            value |= static_cast<std::uint64_t>(std::to_integer<unsigned char>(bytes[at++])) << (i * 8);
        return static_cast<T>(value);
    }
    std::int64_t signedNumber() { return std::bit_cast<std::int64_t>(number<std::uint64_t>()); }
    double real() {
        const auto value = std::bit_cast<double>(number<std::uint64_t>());
        if (!std::isfinite(value)) throw std::runtime_error("Non-finite collector value");
        return value;
    }
    template<class T> T enumeration(std::uint32_t maximum) {
        const auto value = number<std::uint32_t>();
        if (value > maximum) throw std::runtime_error("Invalid collector enumeration");
        return static_cast<T>(value);
    }
    bool boolean() { return enumeration<unsigned>(1) != 0; }
    std::wstring text() {
        const auto length = count(32767, 2);
        std::wstring value; value.reserve(length);
        for (std::uint32_t i = 0; i < length; ++i) value.push_back(static_cast<wchar_t>(number<std::uint16_t>()));
        return value;
    }
    std::uint32_t count(std::size_t maximum, std::size_t minimumBytes) {
        const auto value = number<std::uint32_t>();
        if (value > maximum || value > (bytes.size() - at) / minimumBytes)
            throw std::runtime_error("Invalid collector array length");
        return value;
    }
    void finished() const {
        if (at != bytes.size()) throw std::runtime_error("Unexpected collector trailing data");
    }
private:
    void require(std::size_t size) const {
        if (size > bytes.size() - at) throw std::runtime_error("Truncated collector response");
    }
    std::span<const std::byte> bytes;
    std::size_t at{};
};

inline std::vector<std::byte> encodeHeader(const Header& value) {
    if (value.payloadBytes > maximumPayload) throw std::length_error("Collector response exceeds limit");
    Writer out;
    out.number<std::uint32_t>(0x31434b4e); out.number(protocolVersion);
    out.number(static_cast<std::uint32_t>(value.kind)); out.number(value.status);
    out.number(value.requestId); out.number(value.payloadBytes); out.number<std::uint32_t>(0);
    return std::move(out.bytes);
}

inline Header decodeHeader(std::span<const std::byte> bytes) {
    if (bytes.size() != headerBytes) throw std::runtime_error("Invalid collector header size");
    Reader in(bytes);
    if (in.number<std::uint32_t>() != 0x31434b4e || in.number<std::uint32_t>() != protocolVersion)
        throw std::runtime_error("Invalid collector protocol version");
    Header value;
    const auto kind = in.number<std::uint32_t>();
    if (kind < 1 || kind > 4) throw std::runtime_error("Invalid collector request kind");
    value.kind = static_cast<Kind>(kind); value.status = in.number<std::uint32_t>();
    value.requestId = in.number<std::uint64_t>(); value.payloadBytes = in.number<std::uint32_t>();
    if (!value.requestId || value.status > 1 || value.payloadBytes > maximumPayload || in.number<std::uint32_t>())
        throw std::runtime_error("Invalid collector header fields");
    in.finished(); return value;
}

}
