#pragma once
#include "monitor_api.h"

#ifdef __cplusplus
#define NKS_STARTUP_NOEXCEPT noexcept
extern "C" {
#else
#define NKS_STARTUP_NOEXCEPT
#endif

#define NKS_STARTUP_ABI_VERSION 1u
typedef struct NksStartupList NksStartupList;

typedef enum NksStartupSource {
    NKS_STARTUP_USER_RUN = 0, NKS_STARTUP_USER_RUN_ONCE = 1, NKS_STARTUP_MACHINE_RUN = 2, NKS_STARTUP_MACHINE_RUN_ONCE = 3,
    NKS_STARTUP_MACHINE_RUN32 = 4, NKS_STARTUP_MACHINE_RUN_ONCE32 = 5, NKS_STARTUP_USER_FOLDER = 6, NKS_STARTUP_COMMON_FOLDER = 7
} NksStartupSource;
typedef enum NksStartupState { NKS_STARTUP_ENABLED = 0, NKS_STARTUP_DISABLED = 1, NKS_STARTUP_RUN_ONCE = 2 } NksStartupState;

#pragma pack(push, 8)
typedef struct NksStartupItem {
    NksString16 name, command, location, target;
    uint32_t source, state;
    uint64_t changed_filetime;
} NksStartupItem;

typedef struct NksStartupView {
    uint32_t abi_version, item_count;
    const NksStartupItem* items;
} NksStartupView;
#pragma pack(pop)

// Reads Run/RunOnce values and Startup folders synchronously; call it off the UI thread.
NKS_MONITOR_API NksResult __cdecl nks_startup_read(uint32_t abi_version, NksStartupList** result) NKS_STARTUP_NOEXCEPT;
// Strings and items remain valid until the list is released.
NKS_MONITOR_API NksResult __cdecl nks_startup_get_view(const NksStartupList* list, uint32_t view_size, NksStartupView* result) NKS_STARTUP_NOEXCEPT;
NKS_MONITOR_API void __cdecl nks_startup_release(NksStartupList* list) NKS_STARTUP_NOEXCEPT;
// Run, Run32 and Startup folder items are writable (machine-wide ones need an elevated caller, else ERROR_ACCESS_DENIED);
// RunOnce items are not. The Windows error is stored on NKS_FAILED.
NKS_MONITOR_API NksResult __cdecl nks_startup_set_enabled(uint32_t source, const wchar_t* name, uint32_t length, int32_t enabled,
    uint32_t* win32_error) NKS_STARTUP_NOEXCEPT;

#ifdef __cplusplus
}
#endif
#undef NKS_STARTUP_NOEXCEPT
