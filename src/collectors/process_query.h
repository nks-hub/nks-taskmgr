#pragma once

#include <windows.h>

#ifdef __cplusplus
extern "C" {
#endif

enum { NKS_PROCESS_BUFFER_LIMIT = 64 * 1024 * 1024, NKS_PROCESS_QUERY_ATTEMPTS = 8 };
typedef LONG (NTAPI* NKS_SYSTEM_QUERY)(ULONG, void*, ULONG, ULONG*);

LONG NksQueryProcessBuffer(NKS_SYSTEM_QUERY query, void** buffer, ULONG* capacity, ULONG* used);
void NksFreeProcessBuffer(void* buffer);

#ifdef __cplusplus
}
#endif
