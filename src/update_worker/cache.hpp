#pragma once
#include "core/update_image.hpp"

namespace nks::update {
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
    std::vector<std::wstring> journalFiles() const;
    const std::wstring& path() const noexcept { return root_; }
private:
    std::wstring root_;
    broker::Handle mutex_;
    std::vector<broker::Handle> directories_;
    std::uint64_t usage() const;
};
std::vector<std::byte> readData(const std::wstring& path, std::size_t limit = maxPayload);
void writeNew(const std::wstring& path, std::span<const std::byte> bytes);
std::vector<std::wstring> directoryEntries(const std::wstring& path, std::size_t limit);
}
