#pragma once

#include <windows.h>

#ifdef __cplusplus
extern "C" {
#endif

enum { NKS_PROCESS_BUFFER_LIMIT = 64 * 1024 * 1024, NKS_PROCESS_QUERY_ATTEMPTS = 8 };
typedef LONG (NTAPI* NKS_SYSTEM_QUERY)(ULONG, void*, ULONG, ULONG*);

typedef struct NksProcessQueryBudget {
    ULONG remaining_attempts;
    ULONG allocated_bytes;
} NksProcessQueryBudget;

LONG NksQueryProcessBuffer(NKS_SYSTEM_QUERY query, void** buffer, ULONG* capacity, ULONG* used);
LONG NksQueryProcessBufferBudgeted(NKS_SYSTEM_QUERY query, ULONG informationClass, void** buffer,
    ULONG* capacity, ULONG* used, NksProcessQueryBudget* budget);
void NksFreeProcessBuffer(void* buffer);

#ifdef __cplusplus
}
#endif
