#pragma once

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <type_traits>

namespace nks {

enum class MeasurementUnit : std::uint8_t {
    unknown, percent, bytes, bytesPerSecond, bitsPerSecond, celsius, seconds, count, operationsPerSecond
};
enum class MeasurementNormalization : std::uint8_t { none, wholeMachine, logicalProcessor, busiestGpuEngine, deviceActiveTime };
enum class MeasurementBasis : std::uint8_t { unknown, gauge, elapsedDelta, counterRatio, providerDefined };
enum class MeasurementIntervalState : std::uint8_t { unknown, notApplicable, measured, unavailable };

struct MeasurementMetadata {
    MeasurementUnit unit{};
    MeasurementNormalization normalization{};
    MeasurementBasis basis{};
    MeasurementIntervalState intervalState{};
    std::uint32_t normalizationDivisor{1};
    // Interval is meaningful only when measured; cadence never substitutes for it.
    // Zero observation/cadence means unknown. The metric's own state remains authoritative.
    std::int64_t observedAtNs{}, intervalNs{}, cadenceNs{};
};

struct ProcessMeasurements {
    MeasurementMetadata processCpu, processIo, processMemory, systemCpu, logicalCpu, systemMemory;
};

struct ResourceMeasurements {
    MeasurementMetadata primary, secondary, tertiary, temperature, capacity, sharedCapacity, engineUtilization;
};

static_assert(std::is_trivially_copyable_v<MeasurementMetadata> && sizeof(MeasurementMetadata) == 32);
static_assert(offsetof(MeasurementMetadata, normalizationDivisor) == 4 && offsetof(MeasurementMetadata, observedAtNs) == 8);
static_assert(sizeof(ProcessMeasurements) == 192 && sizeof(ResourceMeasurements) == 224);

std::int64_t measurementTime(std::chrono::steady_clock::time_point time) noexcept;
MeasurementMetadata measurementMetadata(MeasurementUnit unit, MeasurementNormalization normalization, MeasurementBasis basis,
    std::int64_t observedAtNs, std::int64_t previousObservedAtNs = 0, std::uint32_t normalizationDivisor = 1) noexcept;
void setMeasurementInterval(MeasurementMetadata& metadata, std::int64_t intervalNs, bool usable) noexcept;

}
