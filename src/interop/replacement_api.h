#pragma once
#include "monitor_api.h"

#ifdef __cplusplus
#define NKS_REPLACEMENT_NOEXCEPT noexcept
extern "C" {
#else
#define NKS_REPLACEMENT_NOEXCEPT
#endif

typedef enum NksReplacementStatus {
    NKS_REPLACEMENT_NOT_REGISTERED = 0, NKS_REPLACEMENT_THIS_INSTALL = 1,
    NKS_REPLACEMENT_OTHER_NKS = 2, NKS_REPLACEMENT_FOREIGN = 3
} NksReplacementStatus;
typedef enum NksReplacementFlag { NKS_REPLACEMENT_HAS_BACKUP = 1, NKS_REPLACEMENT_BACKUP_RESTORABLE = 2 } NksReplacementFlag;

// Reads the machine-wide Task Manager registration relative to the absolute GUI path; needs no elevation.
// The Debugger command is copied truncated to debugger_capacity UTF-16 units (no terminator) and its full length is returned.
NKS_MONITOR_API NksResult __cdecl nks_replacement_read(const wchar_t* gui_path, uint32_t* status, uint32_t* flags, uint32_t* win32_error,
    wchar_t* debugger, uint32_t debugger_capacity, uint32_t* debugger_length) NKS_REPLACEMENT_NOEXCEPT;

#ifdef __cplusplus
}
#endif
