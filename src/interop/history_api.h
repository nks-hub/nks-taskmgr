#pragma once
#include "monitor_api.h"

#ifdef __cplusplus
#define NKS_HISTORY_NOEXCEPT noexcept
extern "C" {
#else
#define NKS_HISTORY_NOEXCEPT
#endif

#define NKS_HISTORY_ABI_VERSION 2u
typedef struct NksHistoryFrame NksHistoryFrame;

#pragma pack(push, 8)
typedef struct NksHistoryDefinition {
    uint32_t id, channel, group, index;
    NksString16 resource_id, detail_id, name;
} NksHistoryDefinition;

typedef struct NksHistoryPoint {
    uint64_t sequence;
    int64_t time_ns;
    uint32_t series, reserved;
    NksMetric metric;
} NksHistoryPoint;

typedef struct NksHistoryView {
    uint32_t abi_version, reserved;
    uint64_t lost_points, capture_errors;
    const NksHistoryDefinition* definitions;
    uint32_t definition_count, flags;
    const NksHistoryPoint* points;
    uint32_t point_count, more_pending;
} NksHistoryView;
#pragma pack(pop)

// One reader consumes bounded batches. Series IDs are never reassigned within a monitor.
// Data remains owned by the retained frame.
NKS_MONITOR_API NksResult __cdecl nks_monitor_read_history(NksMonitor* monitor, NksHistoryFrame** result) NKS_HISTORY_NOEXCEPT;
NKS_MONITOR_API NksResult __cdecl nks_history_frame_get_view(const NksHistoryFrame* frame, uint32_t size, NksHistoryView* result) NKS_HISTORY_NOEXCEPT;
NKS_MONITOR_API void __cdecl nks_history_frame_retain(NksHistoryFrame* frame) NKS_HISTORY_NOEXCEPT;
NKS_MONITOR_API void __cdecl nks_history_frame_release(NksHistoryFrame* frame) NKS_HISTORY_NOEXCEPT;

#ifdef __cplusplus
}
#endif
#undef NKS_HISTORY_NOEXCEPT
