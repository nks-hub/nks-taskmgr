/*
 * Adapted from PhEnumProcessesEx, phlib/native.c, System Informer
 * 99a0d28e091878967a804ac9ea2fe22f2c1fdadb. See third_party/phnt/LICENSE.
 * The caller owns the buffer; growth is limited to 64 MiB and eight queries.
 */
#include <phnt_windows.h>
#include <phnt.h>
#include "process_query.h"

LONG NksQueryProcessBuffer(NKS_SYSTEM_QUERY query, void** buffer, ULONG* capacity, ULONG* used)
{
    ULONG attempt;
    if (!query || !buffer || !capacity || !used)
        return STATUS_INVALID_PARAMETER;

    *used = 0;
    if ((*buffer == NULL) != (*capacity == 0) || *capacity > NKS_PROCESS_BUFFER_LIMIT)
        return STATUS_INVALID_PARAMETER;

    if (!*buffer)
    {
        *capacity = 0x4000;
        *buffer = HeapAlloc(GetProcessHeap(), 0, *capacity);
        if (!*buffer)
        {
            *capacity = 0;
            return STATUS_NO_MEMORY;
        }
    }

    for (attempt = 0; attempt < NKS_PROCESS_QUERY_ATTEMPTS; ++attempt)
    {
        ULONG required = 0;
        LONG status = query(SystemProcessInformation, *buffer, *capacity, &required);
        if (status == STATUS_BUFFER_TOO_SMALL || status == STATUS_INFO_LENGTH_MISMATCH)
        {
            ULONG growth = *capacity + *capacity / 2;
            ULONG next = required > growth ? required : growth;
            void* replacement;
            if (next > NKS_PROCESS_BUFFER_LIMIT)
                return STATUS_INSUFFICIENT_RESOURCES;
            replacement = HeapAlloc(GetProcessHeap(), 0, next);
            if (!replacement)
                return STATUS_NO_MEMORY;
            HeapFree(GetProcessHeap(), 0, *buffer);
            *buffer = replacement;
            *capacity = next;
        }
        else
        {
            if (!NT_SUCCESS(status))
                return status;
            if (required == 0 || required > *capacity)
                return STATUS_DATA_ERROR;
            *used = required;
            return status;
        }
    }

    return STATUS_RETRY;
}

void NksFreeProcessBuffer(void* buffer)
{
    if (buffer)
        HeapFree(GetProcessHeap(), 0, buffer);
}
