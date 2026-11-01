#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef NKS_MONITOR_EXPORTS
#define NKS_MONITOR_API __declspec(dllexport)
#else
#define NKS_MONITOR_API __declspec(dllimport)
#endif
#ifdef __cplusplus
#define NKS_NOEXCEPT noexcept
extern "C" {
#else
#define NKS_NOEXCEPT
#endif

#define NKS_MONITOR_ABI_VERSION 1u

typedef struct NksMonitor NksMonitor;
typedef struct NksFrame NksFrame;

typedef enum NksResult {
    NKS_OK = 0, NKS_NO_DATA = 1, NKS_INVALID_ARGUMENT = 2,
    NKS_ABI_MISMATCH = 3, NKS_OUT_OF_MEMORY = 4, NKS_FAILED = 5
} NksResult;

typedef enum NksSampleState {
    NKS_SAMPLE_VALID = 0, NKS_SAMPLE_FIRST = 1, NKS_SAMPLE_UNAVAILABLE = 2,
    NKS_SAMPLE_ACCESS_DENIED = 3, NKS_SAMPLE_UNSUPPORTED = 4, NKS_SAMPLE_STALE = 5,
    NKS_SAMPLE_ERROR = 6, NKS_SAMPLE_GAP = 7
} NksSampleState;

typedef enum NksFrameFlag {
    NKS_PROCESS_DATA = 1, NKS_RESOURCE_DATA = 2,
    NKS_PROCESS_ERROR_CHANGED = 4, NKS_RESOURCE_ERROR_CHANGED = 8
} NksFrameFlag;

typedef enum NksGenerationKind {
    NKS_GENERATION_MISSING = 0, NKS_GENERATION_CREATION_TIME = 1, NKS_GENERATION_SEQUENCE_NUMBER = 2
} NksGenerationKind;

typedef enum NksResourceKind {
    NKS_RESOURCE_CPU = 0, NKS_RESOURCE_MEMORY = 1, NKS_RESOURCE_DISK = 2,
    NKS_RESOURCE_GPU = 3, NKS_RESOURCE_NETWORK = 4
} NksResourceKind;

typedef enum NksMetricSource {
    NKS_SOURCE_NATIVE_PROCESS = 0, NKS_SOURCE_PROCESSOR_TIME = 1, NKS_SOURCE_PERFORMANCE_INFO = 2,
    NKS_SOURCE_PERFORMANCE_COUNTER = 3, NKS_SOURCE_NETWORK_INTERFACE = 4, NKS_SOURCE_DXGI = 5, NKS_SOURCE_GPU_ADAPTER = 6
} NksMetricSource;

#pragma pack(push, 8)
typedef struct NksString16 {
    const wchar_t* data;
    uint32_t length;
    uint32_t reserved;
} NksString16;

typedef struct NksMetric {
    double value;
    uint32_t state;
    uint32_t source;
} NksMetric;

typedef struct NksProcess {
    // Legacy wall-clock metadata. Use nks_frame_get_boot_data for identity.
    uint64_t boot_time;
    uint32_t pid, parent_pid;
    uint64_t generation;
    uint32_t generation_kind, threads, handles, reserved;
    uint64_t working_set_bytes, private_bytes;
    uint64_t kernel_100ns, user_100ns, read_io_bytes, write_io_bytes;
    NksMetric cpu_percent, read_io_bytes_per_second, write_io_bytes_per_second;
    NksString16 name;
} NksProcess;

typedef struct NksProcessor {
    uint32_t group, index;
    uint64_t kernel_100ns, user_100ns, idle_100ns;
    NksMetric cpu_percent;
} NksProcessor;

typedef struct NksMemory {
    uint64_t physical_bytes, available_bytes, committed_bytes, commit_limit_bytes;
    uint64_t cache_bytes, paged_pool_bytes, nonpaged_pool_bytes;
    uint64_t page_file_bytes, page_file_used_bytes, page_file_peak_bytes, installed_bytes;
    uint32_t state, page_file_state;
} NksMemory;

typedef struct NksProcessData {
    int64_t measured_at_ns, wall_time_unix_ns;
    int64_t collection_ns, query_ns, parse_ns, system_ns;
    uint32_t logical_processors, processor_state;
    NksMetric cpu_percent;
    NksString16 cpu_model;
    NksMemory memory;
    const NksProcess* processes;
    uint32_t process_count, processor_count;
    const NksProcessor* processors;
} NksProcessData;

typedef struct NksResource {
    uint32_t kind, reserved;
    NksString16 id, title, description, status;
    NksMetric primary, secondary, tertiary;
    double capacity;
} NksResource;

typedef struct NksResourceData {
    int64_t measured_at_ns;
    uint32_t disk_counters_available, gpu_counters_available;
    const NksResource* resources;
    uint32_t resource_count, reserved;
} NksResourceData;

typedef struct NksFrameView {
    uint32_t abi_version, flags;
    int64_t read_at_ns;
    NksString16 process_error, resource_error;
    NksProcessData processes;
    NksResourceData resources;
} NksFrameView;

typedef struct NksGpuEngine {
    NksString16 id, name;
    NksMetric utilization;
} NksGpuEngine;

typedef struct NksGpu {
    NksString16 resource_id;
    double shared_capacity;
    NksMetric temperature;
    const NksGpuEngine* engines;
    uint32_t engine_count, reserved;
} NksGpu;

typedef struct NksGpuData {
    const NksGpu* gpus;
    uint32_t gpu_count, reserved;
} NksGpuData;

typedef struct NksHardwareFact {
    NksString16 resource_id, name, value;
} NksHardwareFact;

typedef struct NksHardwareData {
    const NksHardwareFact* facts;
    uint32_t fact_count, reserved;
} NksHardwareData;

typedef struct NksBootData {
    uint64_t low, high;
    uint32_t state, reserved;
} NksBootData;

typedef struct NksProcessCreation {
    uint32_t pid, generation_kind;
    uint64_t generation, created_100ns;
    uint32_t state, reserved;
} NksProcessCreation;

typedef struct NksProcessCreationData {
    const NksProcessCreation* processes;
    uint32_t process_count, reserved;
} NksProcessCreationData;

typedef struct NksProcessIdentityData {
    uint64_t boot_low, boot_high;
    uint32_t pid, generation_kind;
    uint64_t generation;
    uint32_t state, reserved;
} NksProcessIdentityData;
#pragma pack(pop)

// String lengths count UTF-16 code units. Arrays and strings live until the final frame release.
// A frame retains its snapshots even after its monitor has been destroyed.
NKS_MONITOR_API NksResult __cdecl nks_monitor_create(uint32_t abi_version, uint32_t interval_ms, int32_t paused, NksMonitor** result) NKS_NOEXCEPT;
NKS_MONITOR_API void __cdecl nks_monitor_destroy(NksMonitor* monitor) NKS_NOEXCEPT;
NKS_MONITOR_API NksResult __cdecl nks_monitor_set_paused(NksMonitor* monitor, int32_t paused) NKS_NOEXCEPT;
NKS_MONITOR_API NksResult __cdecl nks_monitor_set_interval(NksMonitor* monitor, uint32_t interval_ms) NKS_NOEXCEPT;
NKS_MONITOR_API NksResult __cdecl nks_monitor_read(NksMonitor* monitor, NksFrame** result) NKS_NOEXCEPT;
NKS_MONITOR_API NksResult __cdecl nks_frame_get_view(const NksFrame* frame, uint32_t view_size, NksFrameView* result) NKS_NOEXCEPT;
NKS_MONITOR_API NksResult __cdecl nks_frame_get_gpu_data(const NksFrame* frame, uint32_t data_size, NksGpuData* result) NKS_NOEXCEPT;
NKS_MONITOR_API NksResult __cdecl nks_frame_get_hardware_data(const NksFrame* frame, uint32_t data_size, NksHardwareData* result) NKS_NOEXCEPT;
NKS_MONITOR_API NksResult __cdecl nks_frame_get_boot_data(const NksFrame* frame, uint32_t data_size, NksBootData* result) NKS_NOEXCEPT;
// Creation times are independent of generation_kind. A missing time has state UNAVAILABLE and value zero.
NKS_MONITOR_API NksResult __cdecl nks_frame_get_process_creation_data(const NksFrame* frame, uint32_t data_size, NksProcessCreationData* result) NKS_NOEXCEPT;
// The caller retains ownership of the process HANDLE. VALID verifies that held object, not action permissions or continued liveness.
NKS_MONITOR_API NksResult __cdecl nks_process_query_identity(void* process_handle, uint32_t generation_kind, uint32_t data_size, NksProcessIdentityData* result) NKS_NOEXCEPT;
NKS_MONITOR_API void __cdecl nks_frame_retain(NksFrame* frame) NKS_NOEXCEPT;
NKS_MONITOR_API void __cdecl nks_frame_release(NksFrame* frame) NKS_NOEXCEPT;
// measured_at_ns and read_at_ns use this clock; wall_time_unix_ns uses the Unix epoch.
NKS_MONITOR_API int64_t __cdecl nks_monotonic_now_ns(void) NKS_NOEXCEPT;

#ifdef __cplusplus
}
#endif
#undef NKS_NOEXCEPT
