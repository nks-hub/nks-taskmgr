#pragma once
#include "core/update_image.hpp"

namespace nks::update {
enum class CacheDeleteState { deleted, absent, busy, changed, denied, error };
struct CacheDeleteTarget {
    HANDLE cacheRoot{}, parent{};
    FILE_ID_INFO rootId{}, parentId{}, id{};
    std::wstring parentRelative, leaf;
    std::uint64_t size{};
    Hash hash{};
};
struct CacheDeleteResult {
    CacheDeleteState state{CacheDeleteState::error};
    DWORD windows{};
    LONG ntStatus{};
    std::uint32_t files{}, directories{};
    std::uint64_t bytes{};
private:
    FILE_ID_INFO stageRoot{}, stageId{};
    std::wstring stageLeaf;
    friend CacheDeleteResult deleteCacheFile(const CacheDeleteTarget& target);
    friend CacheDeleteResult deleteEmptyCacheStage(const CacheDeleteTarget& target, const CacheDeleteResult& removedFile);
};

// The caller holds the cache mutex and keeps the verified root/parent handles alive.
CacheDeleteResult deleteCacheFile(const CacheDeleteTarget& target);
CacheDeleteResult deleteEmptyCacheStage(const CacheDeleteTarget& target, const CacheDeleteResult& removedFile);
}
