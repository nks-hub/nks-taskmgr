#pragma once
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <type_traits>
#include <vector>

namespace nks::service_actions {
constexpr std::uint32_t magic = 0x31534b4e, maximumPayload = 4 * 1024 * 1024, maximumServices = 64;
enum class Command : std::uint16_t { plan = 1, apply = 2, cancel = 3 };
enum class Action : std::uint32_t { start = 1, stop = 2, restart = 3 };
enum class Phase : std::uint32_t { request, plan, accepted, progress, result, error };
enum class Status : std::uint32_t { ok, accessDenied, unsupported, changed, timeout, invalidRequest, limitExceeded, error, cancelled, partial };
enum class Outcome : std::uint32_t { notAttempted, alreadyDesired, confirmed, error, changed, timeout, unknown, cancelled };
enum PlanFlag : std::uint32_t { root = 1, stopRequired = 2, startRequired = 4, initiallyRunning = 8 };
enum ResultFlag : std::uint32_t { stopRequested = 1, stopConfirmed = 2, startRequested = 4, startConfirmed = 8, configVerified = 16, stateKnown = 32, mayHaveApplied = 64 };
#pragma pack(push, 8)
struct Header {
    std::uint32_t magicValue{magic}; std::uint16_t version{1}; Command command{};
    std::uint32_t payloadBytes{}, count{}; std::uint64_t requestId{}, nonceLow{}, nonceHigh{};
    Action action{}; Phase phase{}; Status status{}; std::uint32_t error{}; std::uint64_t reserved{};
};
struct PlanRow {
    std::uint32_t id{}, flags{}, originalState{}, desiredState{}, stopOrder{UINT32_MAX}, startOrder{UINT32_MAX};
    std::uint32_t nameChars{}, titleChars{}, commandChars{}, accountChars{}, dependencyCount{}, startType{}, serviceType{}, reserved{};
};
struct ResultRow {
    std::uint32_t id{}; Outcome outcome{}; std::uint32_t lastState{}, observationError{}, flags{}, stopError{}, startError{}, checkpoint{};
    std::uint32_t waitHint{}, win32Exit{}, serviceExit{}, reserved{}; std::int64_t observedAtNs{};
};
#pragma pack(pop)
static_assert(std::endian::native == std::endian::little && sizeof(wchar_t) == 2);
static_assert(sizeof(Header) == 64 && sizeof(PlanRow) == 56 && sizeof(ResultRow) == 56);

inline bool validText(std::wstring_view value) {
    if (value.size() > 32767) return false;
    for (std::size_t i = 0; i < value.size(); ++i) {
        const auto unit = static_cast<std::uint16_t>(value[i]);
        if (!unit) return false;
        if (unit >= 0xd800 && unit <= 0xdbff) {
            if (++i == value.size() || value[i] < 0xdc00 || value[i] > 0xdfff) return false;
        } else if (unit >= 0xdc00 && unit <= 0xdfff) return false;
    }
    return true;
}
class Writer {
public:
    template<class T> void append(const T& value) { static_assert(std::is_trivially_copyable_v<T>); raw(&value, sizeof(value)); }
    void text(std::wstring_view value, bool prefix = true) {
        if (!validText(value)) throw std::invalid_argument("Invalid service UTF-16");
        if (prefix) append(static_cast<std::uint32_t>(value.size()));
        raw(value.data(), value.size() * sizeof(wchar_t));
    }
    std::vector<std::byte> bytes;
private:
    void raw(const void* source, std::size_t size) {
        if (size > maximumPayload - bytes.size()) throw std::length_error("Service action payload limit exceeded");
        if (!size) return;
        const auto* from = static_cast<const std::byte*>(source); bytes.insert(bytes.end(), from, from + size);
    }
};
class Reader {
public:
    explicit Reader(std::span<const std::byte> source) : bytes(source) { if (source.size() > maximumPayload) throw std::length_error("Service action payload limit exceeded"); }
    template<class T> T value() {
        static_assert(std::is_trivially_copyable_v<T>);
        if (sizeof(T) > bytes.size() - at) throw std::invalid_argument("Truncated service action payload");
        T result{}; std::memcpy(&result, bytes.data() + at, sizeof(result)); at += sizeof(result); return result;
    }
    std::wstring text() {
        const auto count = value<std::uint32_t>();
        if (count > 32767 || count > (bytes.size() - at) / 2) throw std::invalid_argument("Invalid service action text length");
        std::wstring result(count, L'\0'); if (count) std::memcpy(result.data(), bytes.data() + at, count * 2); at += count * 2;
        if (!validText(result)) throw std::invalid_argument("Invalid service action UTF-16"); return result;
    }
    void finished() const { if (at != bytes.size()) throw std::invalid_argument("Trailing service action payload"); }
private:
    std::span<const std::byte> bytes; std::size_t at{};
};
}
