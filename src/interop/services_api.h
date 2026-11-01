#pragma once
#include "monitor_api.h"

#ifdef __cplusplus
#define NKS_SERVICES_NOEXCEPT noexcept
extern "C" {
#else
#define NKS_SERVICES_NOEXCEPT
#endif

#define NKS_SERVICES_ABI_VERSION 1u
typedef struct NksServices NksServices;
typedef struct NksServiceFrame NksServiceFrame;

typedef enum NksServiceFrameFlag {
    NKS_SERVICES_DATA = 1, NKS_SERVICE_DETAIL = 2,
    NKS_SERVICES_ERROR_CHANGED = 4, NKS_SERVICE_DETAIL_ERROR_CHANGED = 8
} NksServiceFrameFlag;

#pragma pack(push, 8)
typedef struct NksServiceConfig {
    uint32_t error, start_type, delayed_error, delayed;
    NksString16 account, group, command;
    const NksString16* dependencies;
    uint32_t dependency_count, reserved;
} NksServiceConfig;

typedef struct NksService {
    NksString16 name, title;
    uint32_t state, pid, type, accepted_controls;
    NksServiceConfig config;
} NksService;

typedef struct NksServiceData {
    int64_t measured_at_ns, collection_ns;
    const NksService* services;
    uint32_t service_count, reserved;
} NksServiceData;

typedef struct NksServiceDetail {
    int64_t measured_at_ns;
    NksString16 name, description;
    NksServiceConfig config;
    uint32_t status_error, description_error, dependents_error, host_error;
    uint32_t pid, state;
    uint64_t host_created_100ns;
    const NksString16* dependents;
    uint32_t dependent_count, reserved;
} NksServiceDetail;

typedef struct NksServiceFrameView {
    uint32_t abi_version, flags;
    int64_t read_at_ns;
    NksString16 error, detail_error;
    NksServiceData data;
    NksServiceDetail detail;
} NksServiceFrameView;
#pragma pack(pop)

// Collection starts inactive. Selection is a counted UTF-16 SCM name, not a display name.
NKS_MONITOR_API NksResult __cdecl nks_services_create(uint32_t abi_version, NksServices** result) NKS_SERVICES_NOEXCEPT;
NKS_MONITOR_API void __cdecl nks_services_destroy(NksServices* services) NKS_SERVICES_NOEXCEPT;
NKS_MONITOR_API NksResult __cdecl nks_services_set_active(NksServices* services, int32_t active) NKS_SERVICES_NOEXCEPT;
NKS_MONITOR_API NksResult __cdecl nks_services_set_paused(NksServices* services, int32_t paused) NKS_SERVICES_NOEXCEPT;
NKS_MONITOR_API NksResult __cdecl nks_services_select(NksServices* services, const wchar_t* name, uint32_t length) NKS_SERVICES_NOEXCEPT;
NKS_MONITOR_API NksResult __cdecl nks_services_read(NksServices* services, NksServiceFrame** result) NKS_SERVICES_NOEXCEPT;
// Arrays and UTF-16 strings remain valid until the final frame release, including after monitor destruction.
NKS_MONITOR_API NksResult __cdecl nks_service_frame_get_view(const NksServiceFrame* frame, uint32_t view_size, NksServiceFrameView* result) NKS_SERVICES_NOEXCEPT;
// Host identities are verified against host_created_100ns and copied into the immutable frame.
NKS_MONITOR_API NksResult __cdecl nks_service_frame_get_host_identity(const NksServiceFrame* frame, uint32_t generation_kind, uint32_t data_size, NksProcessIdentityData* result) NKS_SERVICES_NOEXCEPT;
NKS_MONITOR_API void __cdecl nks_service_frame_retain(NksServiceFrame* frame) NKS_SERVICES_NOEXCEPT;
NKS_MONITOR_API void __cdecl nks_service_frame_release(NksServiceFrame* frame) NKS_SERVICES_NOEXCEPT;

#ifdef __cplusplus
}
#endif
#undef NKS_SERVICES_NOEXCEPT
