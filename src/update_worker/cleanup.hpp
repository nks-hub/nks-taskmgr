#pragma once
#include "execute.hpp"
#include "cache_delete.hpp"

namespace nks::update {
enum class CleanupCategory : std::uint32_t { package = 1, plan, other };
enum class CleanupOutcome : std::uint32_t { eligible, kept, deleted, skipped, failed, unknown };
enum class CleanupReason : std::uint32_t {
    unused, current, previous, liveWorker, liveInstaller, lifetimeUnknown, unresolved, resolutionProof, rootAnchor,
    unknownRoot, unknownData, missingProof, stageInUse, changed, denied, io, dependency, cancelled, legacy, alreadyAbsent
};
struct CleanupFile {
    CachePartition partition{};
    std::wstring leaf;
    Proof expected;
    bool present{true};
};
struct CleanupItem {
    Hash id{};
    PlanId plan{};
    CleanupCategory category{};
    CleanupOutcome outcome{CleanupOutcome::kept};
    CleanupReason reason{CleanupReason::unknownData};
    bool sizeKnown{};
    std::uint64_t bytes{}, deletedBytes{};
    std::uint32_t files{}, directories{}, deletedFiles{}, deletedDirectories{};
    DWORD windows{}; LONG ntStatus{};
    FILE_ID_INFO scope{}, stageId{};
    bool hasStage{};
    std::wstring name, detail, path;
    std::vector<CleanupFile> objects;
};
struct CleanupPlan {
    Hash manifest{};
    FILE_ID_INFO root{};
    ULONGLONG deadline{};
    bool complete{true}, confirmed{}, mayHaveDeleted{};
    std::vector<CleanupItem> items;
    std::vector<Image> rootGuards;
};
void rememberRoot(Cache& cache, const Scope& scope);
CleanupPlan prepareCleanup(Cache& cache, const Context& context, ULONGLONG requestDeadline = 0);
void applyCleanup(Cache& cache, const Context& context, CleanupPlan& plan);
std::vector<std::byte> cleanupPayload(const CleanupPlan& plan, bool accepted = false, Status status = Status::ok, DWORD windows = 0);
}
