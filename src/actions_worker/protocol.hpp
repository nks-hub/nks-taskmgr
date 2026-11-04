#pragma once
#include <cstddef>
#include <cstdint>
#include <span>
#include <stdexcept>

namespace nks::actions {
constexpr std::uint32_t magic = 0x31414b4e;
constexpr std::uint16_t version = 1;
constexpr std::size_t headerBytes = 64, targetBytes = 48, resultBytes = 112, maximumTargets = 64;
constexpr std::size_t maximumTreeTargets = 256, treePrefixBytes = 64, treeNodeBytes = 64;
constexpr std::size_t maximumCpuSets = 4096, cpuSetsPrefixBytes = 48, cpuSetBytes = 32;
constexpr std::size_t maximumCpuSetsResponse = resultBytes + cpuSetsPrefixBytes + maximumCpuSets * (cpuSetBytes + 4);
constexpr std::size_t handleSpecBytes = 48, maximumHandleTypeChars = 128, maximumHandleNameChars = 32767;
constexpr std::uint32_t verifyHandleName = 1;

enum class Command : std::uint16_t { query, closeWindows, terminate, suspend, resume, priority, affinity, terminateTree, queryCpuSets = 8, cpuSets = 9, closeHandle = 10, taskManager = 11 };
enum class FrameKind : std::uint32_t { accepted, results, rejected, failed, treePlan, treeResults, cpuSets = 6 };
enum class Outcome : std::uint32_t {
    inspected, applied, closeRequested, terminationRequested, nativeRequestAccepted, refused,
    identityChanged, alreadyExited, error, unsupported, unverified, notPerformed
};
enum class Reason : std::uint32_t {
    none, invalidTarget, ownOrParent, systemTarget, critical, protectedTarget, ownerMismatch,
    accessDenied, identityUnverifiable, rawTimeMismatch, noWindows, windowMismatch, invalidArgument,
    multipleGroups, partialClose, readbackMismatch, protectionUnverifiable, parentUnavailable,
    alreadySame, windowLimit, nativeUnavailable, treeBoundary, treeBudget, treeCensus, treeUncertain,
    handleMissing, handleChanged, handleUnverifiable, handleProtected
};
enum TreeFlag : std::uint32_t { nonAtomic = 1, targetLimit = 2, timeLimit = 4, passLimit = 8, censusFailed = 16, parentLost = 32, residual = 64 };
enum Flag : std::uint32_t {
    identityVerified = 1, aliveBefore = 2, aliveAfter = 4, exitedAfter = 8,
    priorityAvailable = 16, affinityAvailable = 32, effectConfirmed = 64, mayHaveApplied = 128,
    objectMatched = 256 // closeHandle only: a non-empty name or a fingerprint tied the closed object to the confirmed one
};

inline std::uint64_t read(std::span<const std::byte> bytes, std::size_t at, std::size_t size)
{
    if (at > bytes.size() || size > bytes.size() - at || size > 8) throw std::invalid_argument("Invalid action frame");
    std::uint64_t value = 0;
    for (std::size_t i = 0; i < size; ++i) value |= static_cast<std::uint64_t>(std::to_integer<unsigned>(bytes[at + i])) << (i * 8);
    return value;
}
inline std::uint32_t read32(std::span<const std::byte> bytes, std::size_t at) { return static_cast<std::uint32_t>(read(bytes, at, 4)); }
inline std::uint64_t read64(std::span<const std::byte> bytes, std::size_t at) { return read(bytes, at, 8); }
inline void write(std::span<std::byte> bytes, std::size_t at, std::uint64_t value, std::size_t size)
{
    if (at > bytes.size() || size > bytes.size() - at || size > 8) throw std::invalid_argument("Invalid action response");
    for (std::size_t i = 0; i < size; ++i) bytes[at + i] = static_cast<std::byte>((value >> (i * 8)) & 0xff);
}
}
