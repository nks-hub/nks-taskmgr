#pragma once
#include "monitor_api.h"

#ifdef __cplusplus
#define NKS_MEASUREMENT_NOEXCEPT noexcept
extern "C" {
#else
#define NKS_MEASUREMENT_NOEXCEPT
#endif

#define NKS_MEASUREMENT_ABI_VERSION 1u
typedef enum NksMeasurementUnit {
    NKS_UNIT_UNKNOWN, NKS_UNIT_PERCENT, NKS_UNIT_BYTES, NKS_UNIT_BYTES_PER_SECOND,
    NKS_UNIT_BITS_PER_SECOND, NKS_UNIT_CELSIUS, NKS_UNIT_SECONDS, NKS_UNIT_COUNT, NKS_UNIT_OPERATIONS_PER_SECOND
} NksMeasurementUnit;
typedef enum NksMeasurementNormalization {
    NKS_NORMALIZATION_NONE, NKS_NORMALIZATION_WHOLE_MACHINE, NKS_NORMALIZATION_LOGICAL_PROCESSOR,
    NKS_NORMALIZATION_BUSIEST_GPU_ENGINE, NKS_NORMALIZATION_DEVICE_ACTIVE_TIME
} NksMeasurementNormalization;
typedef enum NksMeasurementBasis {
    NKS_BASIS_UNKNOWN, NKS_BASIS_GAUGE, NKS_BASIS_ELAPSED_DELTA, NKS_BASIS_COUNTER_RATIO, NKS_BASIS_PROVIDER_DEFINED
} NksMeasurementBasis;
typedef enum NksMeasurementIntervalState {
    NKS_INTERVAL_UNKNOWN, NKS_INTERVAL_NOT_APPLICABLE, NKS_INTERVAL_MEASURED, NKS_INTERVAL_UNAVAILABLE
} NksMeasurementIntervalState;

#pragma pack(push, 8)
typedef struct NksMeasurementMetadata {
    uint8_t unit, normalization, basis, interval_state;
    uint32_t normalization_divisor;
    int64_t observed_at_ns, interval_ns, cadence_ns;
} NksMeasurementMetadata;
typedef struct NksProcessMeasurements {
    NksMeasurementMetadata process_cpu, process_io, process_memory, system_cpu, logical_cpu, system_memory;
} NksProcessMeasurements;
typedef struct NksResourceMeasurements {
    NksString16 resource_id;
    NksMeasurementMetadata primary, secondary, tertiary, temperature, capacity, shared_capacity, engine_utilization;
} NksResourceMeasurements;
typedef struct NksMeasurementData {
    uint32_t abi_version, flags;
    NksProcessMeasurements processes;
    const NksResourceMeasurements* resources;
    uint32_t resource_count, reserved;
} NksMeasurementData;
#pragma pack(pop)

// Times use the frame's monotonic clock. Cadence is observation spacing, not a rate's calculation interval.
// Arrays and resource identifiers remain valid until the final frame release.
NKS_MONITOR_API NksResult __cdecl nks_frame_get_measurement_data(const NksFrame* frame, uint32_t data_size, NksMeasurementData* result) NKS_MEASUREMENT_NOEXCEPT;

#ifdef __cplusplus
}
#endif
#undef NKS_MEASUREMENT_NOEXCEPT
