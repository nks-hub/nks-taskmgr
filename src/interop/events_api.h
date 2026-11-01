#pragma once
#include "monitor_api.h"

#ifdef __cplusplus
#define NKS_EVENTS_NOEXCEPT noexcept
extern "C" {
#else
#define NKS_EVENTS_NOEXCEPT
#endif

typedef struct NksEventMonitor NksEventMonitor;
typedef struct NksEventFrame NksEventFrame;

typedef enum NksEventMonitorState {
    NKS_EVENTS_IDLE = 0, NKS_EVENTS_STARTING = 1, NKS_EVENTS_RUNNING = 2, NKS_EVENTS_STOPPING = 3,
    NKS_EVENTS_STOPPED = 4, NKS_EVENTS_ACCESS_DENIED = 5, NKS_EVENTS_UNSUPPORTED = 6,
    NKS_EVENTS_ERROR = 7, NKS_EVENTS_BUSY = 8, NKS_EVENTS_STOP_TIMED_OUT = 9, NKS_EVENTS_CLEANUP_FAILED = 10
} NksEventMonitorState;
typedef enum NksProcessEventKind { NKS_EVENT_STARTED = 1, NKS_EVENT_STOPPED = 2 } NksProcessEventKind;
typedef enum NksProcessEventFlag {
    NKS_EVENT_HAS_EXIT_STATUS = 1, NKS_EVENT_HAS_IMAGE_NAME = 2, NKS_EVENT_PARTIAL = 4, NKS_EVENT_HAS_PARENT_PID = 8
} NksProcessEventFlag;
typedef enum NksEventFrameFlag { NKS_EVENTS_RESYNC_REQUIRED = 1, NKS_EVENTS_CLEANUP_OUTSTANDING = 2 } NksEventFrameFlag;

#pragma pack(push, 8)
typedef struct NksProcessEvent {
    uint64_t sequence;
    int64_t timestamp_qpc, observed_at_ns;
    uint32_t kind, pid, parent_pid, identity_state;
    uint32_t generation_kind, flags;
    uint64_t generation;
    int32_t exit_status;
    uint32_t reserved;
    NksString16 image_name;
} NksProcessEvent;

typedef struct NksEventFrameView {
    uint32_t abi_version, state, win32_error, flags;
    uint64_t boot_low, boot_high;
    uint32_t boot_state, reserved;
    int64_t qpc_frequency;
    uint64_t provider_events_lost, provider_buffers_lost, queue_dropped, decode_errors, ambiguous, resync_version;
    const NksProcessEvent* events;
    uint32_t event_count, reserved2;
} NksEventFrameView;
#pragma pack(pop)

NKS_MONITOR_API NksResult nks_events_create(NksEventMonitor** monitor) NKS_EVENTS_NOEXCEPT;
NKS_MONITOR_API NksResult nks_events_start(NksEventMonitor* monitor) NKS_EVENTS_NOEXCEPT;
NKS_MONITOR_API void nks_events_stop(NksEventMonitor* monitor) NKS_EVENTS_NOEXCEPT;
NKS_MONITOR_API void nks_events_release(NksEventMonitor* monitor) NKS_EVENTS_NOEXCEPT;
NKS_MONITOR_API NksResult nks_events_read(NksEventMonitor* monitor, NksEventFrame** frame) NKS_EVENTS_NOEXCEPT;
NKS_MONITOR_API void nks_events_ack_resync(NksEventMonitor* monitor, uint64_t version) NKS_EVENTS_NOEXCEPT;
NKS_MONITOR_API const NksEventFrameView* nks_events_frame_view(const NksEventFrame* frame) NKS_EVENTS_NOEXCEPT;
NKS_MONITOR_API void nks_events_frame_retain(NksEventFrame* frame) NKS_EVENTS_NOEXCEPT;
NKS_MONITOR_API void nks_events_frame_release(NksEventFrame* frame) NKS_EVENTS_NOEXCEPT;

#ifdef __cplusplus
}
#endif
#undef NKS_EVENTS_NOEXCEPT
