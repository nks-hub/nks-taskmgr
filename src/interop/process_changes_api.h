#pragma once
#include "monitor_api.h"

#ifdef __cplusplus
#define NKS_CHANGES_NOEXCEPT noexcept
extern "C" {
#else
#define NKS_CHANGES_NOEXCEPT
#endif

#define NKS_PROCESS_CHANGES_ABI_VERSION 1u
#define NKS_PROCESS_CHANGES_RESET 1u
#pragma pack(push, 8)
typedef struct NksProcessChange {
    uint32_t kind, current_index, pid, generation_kind;
    uint64_t generation;
} NksProcessChange;
typedef struct NksProcessChangesData {
    uint32_t abi_version, flags;
    uint64_t revision, base_revision;
    const NksProcessChange* changes;
    uint32_t change_count, process_count;
} NksProcessChangesData;
typedef struct NksProcessLossData {
    uint64_t dropped_snapshots;
} NksProcessLossData;
#pragma pack(pop)

// Revisions follow delivered process frames. On a reset or a missing base, use that frame's full view.
// Entries and indexed current rows remain valid until the final frame release.
NKS_MONITOR_API NksResult __cdecl nks_frame_get_process_changes(const NksFrame* frame, uint32_t size, NksProcessChangesData* data) NKS_CHANGES_NOEXCEPT;
// dropped_snapshots counts process snapshots published but overwritten before the read preceding this frame. It does not renumber revisions.
NKS_MONITOR_API NksResult __cdecl nks_frame_get_process_loss_data(const NksFrame* frame, uint32_t size, NksProcessLossData* data) NKS_CHANGES_NOEXCEPT;

#ifdef __cplusplus
}
#endif
#undef NKS_CHANGES_NOEXCEPT
