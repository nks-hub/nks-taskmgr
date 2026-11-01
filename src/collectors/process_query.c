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
    NksProcessQueryBudget budget;
    if (!capacity)
        return STATUS_INVALID_PARAMETER;
    budget.remaining_attempts = NKS_PROCESS_QUERY_ATTEMPTS;
    budget.allocated_bytes = *capacity;
    return NksQueryProcessBufferBudgeted(query, SystemProcessInformation, buffer, capacity, used, &budget);
}

LONG NksQueryProcessBufferBudgeted(NKS_SYSTEM_QUERY query, ULONG informationClass, void** buffer,
    ULONG* capacity, ULONG* used, NksProcessQueryBudget* budget)
{
    if (!query || !buffer || !capacity || !used || !budget)
        return STATUS_INVALID_PARAMETER;

    *used = 0;
    if ((*buffer == NULL) != (*capacity == 0) || *capacity > budget->allocated_bytes
        || budget->allocated_bytes > NKS_PROCESS_BUFFER_LIMIT || budget->remaining_attempts > NKS_PROCESS_QUERY_ATTEMPTS)
        return STATUS_INVALID_PARAMETER;
    if (!budget->remaining_attempts)
        return STATUS_RETRY;

    if (!*buffer)
    {
        const ULONG initial = 0x4000;
        if (initial > NKS_PROCESS_BUFFER_LIMIT - budget->allocated_bytes)
            return STATUS_INSUFFICIENT_RESOURCES;
        *buffer = HeapAlloc(GetProcessHeap(), 0, initial);
        if (!*buffer)
            return STATUS_NO_MEMORY;
        *capacity = initial;
        budget->allocated_bytes += initial;
    }

    while (budget->remaining_attempts)
    {
        ULONG required = 0;
        LONG status;
        --budget->remaining_attempts;
        status = query(informationClass, *buffer, *capacity, &required);
        if (status == STATUS_BUFFER_TOO_SMALL || status == STATUS_INFO_LENGTH_MISMATCH)
        {
            ULONG growth = *capacity + *capacity / 2;
            ULONG next = required > growth ? required : growth;
            void* replacement;
            if (!budget->remaining_attempts)
                return STATUS_RETRY;
            if (next > NKS_PROCESS_BUFFER_LIMIT - (budget->allocated_bytes - *capacity))
                return STATUS_INSUFFICIENT_RESOURCES;
            replacement = HeapReAlloc(GetProcessHeap(), 0, *buffer, next);
            if (!replacement)
                return STATUS_NO_MEMORY;
            budget->allocated_bytes += next - *capacity;
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
