#include "measurement.hpp"

namespace nks {

std::int64_t measurementTime(std::chrono::steady_clock::time_point time) noexcept
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(time.time_since_epoch()).count();
}

MeasurementMetadata measurementMetadata(MeasurementUnit unit, MeasurementNormalization normalization, MeasurementBasis basis,
    std::int64_t observedAtNs, std::int64_t previousObservedAtNs, std::uint32_t normalizationDivisor) noexcept
{
    MeasurementMetadata result;
    result.unit = unit; result.normalization = normalization; result.basis = basis;
    result.normalizationDivisor = normalizationDivisor; result.observedAtNs = observedAtNs;
    if (observedAtNs > previousObservedAtNs && previousObservedAtNs > 0) result.cadenceNs = observedAtNs - previousObservedAtNs;
    result.intervalState = basis == MeasurementBasis::gauge ? MeasurementIntervalState::notApplicable
        : basis == MeasurementBasis::elapsedDelta ? MeasurementIntervalState::unavailable : MeasurementIntervalState::unknown;
    return result;
}

void setMeasurementInterval(MeasurementMetadata& metadata, std::int64_t intervalNs, bool usable) noexcept
{
    if (metadata.basis != MeasurementBasis::elapsedDelta && metadata.basis != MeasurementBasis::counterRatio) return;
    metadata.intervalState = usable && intervalNs > 0 ? MeasurementIntervalState::measured : MeasurementIntervalState::unavailable;
    metadata.intervalNs = metadata.intervalState == MeasurementIntervalState::measured ? intervalNs : 0;
}

}
