#pragma once
#include "core/update_image.hpp"

namespace nks::update {
enum class CachePartition : std::size_t { packages, plans, stages, journal, roots };
class Cache {
public:
    Cache();
    std::wstring packagePath(const Hash& hash) const;
    std::wstring stagePath(const PlanId& id) const;
    std::wstring planPath(const PlanId& id) const;
    std::wstring journalPath(const PlanId& id, unsigned step) const;
    std::vector<Image> packages() const;
    Image store(Image& source);
    Image stage(Image& self, const PlanId& id);
    std::vector<std::wstring> journalFiles(std::size_t limit = maxJournalFiles) const;
    const std::wstring& path() const noexcept { return root_; }
    HANDLE rootHandle() const noexcept { return rootHandle_.get(); }
    HANDLE parentHandle(CachePartition part) const noexcept { return partitions_[static_cast<std::size_t>(part)].get(); }
    FILE_ID_INFO rootId() const;
    FILE_ID_INFO parentId(CachePartition part) const;
private:
    std::wstring root_;
    broker::Handle mutex_;
    broker::Handle rootHandle_;
    std::array<broker::Handle, 5> partitions_;
    std::vector<broker::Handle> directories_;
    std::uint64_t usage() const;
};
std::vector<std::byte> readData(const std::wstring& path, std::size_t limit = maxPayload);
void writeNew(const std::wstring& path, std::span<const std::byte> bytes);
std::vector<std::wstring> directoryEntries(const std::wstring& path, std::size_t limit);
}
