#include <phnt_windows.h>
#include <phnt.h>
#include "update_worker/cleanup.hpp"
#include "update_worker/cache_delete.hpp"
#include "update_worker/cache.hpp"
#include <algorithm>
#include <atomic>
#include <filesystem>
#include <fstream>
#include <thread>
#include <vector>
#include <iostream>
#include <stdexcept>

namespace {
using namespace nks::update;
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
Scope scope(unsigned id, InstallMode mode = InstallMode::installed)
{
    Scope value; value.mode = mode; value.id.VolumeSerialNumber = 1; value.id.FileId.Identifier[0] = static_cast<BYTE>(id);
    value.path = L"\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\Application";
    return value;
}
JournalEntry entry(unsigned id, std::uint64_t time, const Scope& root, Action action)
{
    JournalEntry value; value.plan.id[0] = static_cast<std::byte>(id); value.time = time;
    value.state = State::verified; value.resultVerified = true; value.plan.action = action; value.plan.root = root.id;
    value.plan.flags = root.mode == InstallMode::portable ? 11u : 7u;
    value.plan.strings[0] = root.path; value.plan.strings[2] = L"2.0.0+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    value.plan.strings[3] = L"1.0.0+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    return value;
}
void verify()
{
    const auto root = scope(1);
    Image installed; installed.version = L"2.0.0+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    const auto normal = entry(20, 20, root, Action::update);
    const auto newer = entry(30, 30, root, Action::rollback);
    auto repair = entry(1, 40, root, Action::repair);
    repair.plan.strings[3] = L"3.0.0+cccccccccccccccccccccccccccccccccccccccc";
    std::vector<JournalEntry> entries{repair, normal, newer};
    const auto previous = previousVersion(entries, installed, root);
    const auto anchor = rollbackJournalAnchor(entries, installed.version, previous, root);
    require(anchor == newer.plan.id, "A lexically earlier verified repair displaced the actual rollback journal");
    std::erase_if(entries, [&](const auto& value) { return value.plan.id != anchor && value.plan.action != Action::repair; });
    require(previousVersion(entries, installed, root) == previous, "Retaining the rollback anchor lost the previous version after cleanup");
    require(rollbackJournalAnchor(entries, installed.version, previous, root) == anchor, "A subsequent cleanup changed the necessary rollback anchor");

    auto foreign = entry(40, 100, scope(2), Action::update);
    auto differentMode = entry(50, 110, scope(1, InstallMode::portable), Action::update);
    auto unverified = entry(60, 120, root, Action::update); unverified.resultVerified = false;
    auto incomplete = entry(70, 130, root, Action::update); incomplete.state = State::installing;
    auto legacy = entry(80, 140, root, Action::update); legacy.legacy = true;
    auto unrelated = entry(90, 150, root, Action::update); unrelated.plan.strings[2] = L"4.0.0+dddddddddddddddddddddddddddddddddddddddd";
    entries.insert(entries.end(), {foreign, differentMode, unverified, incomplete, legacy, unrelated});
    require(rollbackJournalAnchor(entries, installed.version, previous, root) == anchor, "An unrelated or unproven journal became the rollback anchor");
    require(rollbackJournalAnchor(entries, installed.version, L"", root) == PlanId{}, "A missing previous version invented a rollback anchor");
    require(rollbackJournalAnchor({repair}, installed.version, previous, root) == PlanId{}, "Repair-only history invented a rollback predecessor");
}

// Real files in a private cache tree: deletion must remove exactly the verified file and nothing that changed,
// is in use, has a second name or was already taken by a concurrent cleanup.
struct Directory {
    HANDLE value{INVALID_HANDLE_VALUE};
    FILE_ID_INFO id{};
    explicit Directory(const std::filesystem::path& path) {
        value = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES | FILE_TRAVERSE | READ_CONTROL | SYNCHRONIZE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        require(value != INVALID_HANDLE_VALUE && GetFileInformationByHandleEx(value, FileIdInfo, &id, sizeof(id)), "Fixture directory unavailable");
    }
    ~Directory() { close(); }
    void close() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); value = INVALID_HANDLE_VALUE; }
};
struct Fixture {
    std::filesystem::path root;
    Fixture() {
        wchar_t temporary[MAX_PATH + 1]{};
        require(GetTempPathW(MAX_PATH, temporary) != 0, "Temporary directory unavailable");
        root = std::filesystem::path(temporary) / (L"nks-cache-delete-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
        std::filesystem::create_directories(root / L"Packages");
    }
    ~Fixture() { std::error_code ignored; std::filesystem::remove_all(root, ignored); }
    CacheDeleteTarget package(const Directory& cache, const Directory& parent, const std::string& bytes, std::filesystem::path& file) {
        const auto temporary = root / L"Packages" / L"incoming.tmp";
        { std::ofstream out(temporary, std::ios::binary); out << bytes; }
        HANDLE handle = CreateFileW(temporary.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
        require(handle != INVALID_HANDLE_VALUE, "Fixture package unavailable");
        CacheDeleteTarget target; target.cacheRoot = cache.value; target.parent = parent.value;
        target.rootId = cache.id; target.parentId = parent.id; target.parentRelative = L"Packages"; target.size = bytes.size();
        target.hash = hashFile(handle, bytes.size());
        require(GetFileInformationByHandleEx(handle, FileIdInfo, &target.id, sizeof(target.id)), "Fixture package identity unavailable");
        CloseHandle(handle);
        target.leaf = hex(target.hash) + L".exe"; file = root / L"Packages" / target.leaf;
        std::filesystem::rename(temporary, file);
        return target;
    }
};
void realDeletion()
{
    Fixture fixture;
    Directory cache(fixture.root), packages(fixture.root / L"Packages");
    std::filesystem::path file;

    auto target = fixture.package(cache, packages, std::string(4096, 'a'), file);
    auto result = deleteCacheFile(target);
    require(result.state == CacheDeleteState::deleted && result.files == 1 && result.bytes == 4096 && !std::filesystem::exists(file),
        "A verified package was not deleted exactly once");
    require(deleteCacheFile(target).state == CacheDeleteState::absent, "A missing package was not reported as absent");

    target = fixture.package(cache, packages, std::string(4096, 'b'), file);
    HANDLE user = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    require(user != INVALID_HANDLE_VALUE, "Fixture user handle unavailable");
    result = deleteCacheFile(target);
    CloseHandle(user);
    require(result.state == CacheDeleteState::busy && std::filesystem::exists(file), "A package in use was deleted or not reported busy");
    require(deleteCacheFile(target).state == CacheDeleteState::deleted, "A package released by its user could not be deleted");

    target = fixture.package(cache, packages, std::string(4096, 'c'), file);
    { std::fstream out(file, std::ios::binary | std::ios::in | std::ios::out); out.seekp(100); out.put('X'); }
    require(deleteCacheFile(target).state == CacheDeleteState::changed && std::filesystem::exists(file), "Changed package content was deleted");
    std::filesystem::remove(file);

    target = fixture.package(cache, packages, std::string(4096, 'd'), file);
    { std::ofstream out(file, std::ios::binary | std::ios::app); out << 'e'; }
    require(deleteCacheFile(target).state == CacheDeleteState::changed && std::filesystem::exists(file), "A package of a different size was deleted");
    std::filesystem::remove(file);

    target = fixture.package(cache, packages, std::string(4096, 'f'), file);
    const auto alias = fixture.root / L"alias.exe";
    require(CreateHardLinkW(alias.c_str(), file.c_str(), nullptr) != FALSE, "Fixture hard link unavailable");
    require(deleteCacheFile(target).state == CacheDeleteState::changed && std::filesystem::exists(file) && std::filesystem::exists(alias),
        "A package with a second name was deleted");
    std::filesystem::remove(alias); std::filesystem::remove(file);

    unsigned deleted{}, other{};
    for (int round = 0; round < 50; ++round) {
        target = fixture.package(cache, packages, std::string(65536, static_cast<char>('g' + round % 10)), file);
        std::atomic<int> ready{};
        CacheDeleteResult results[2];
        std::vector<std::thread> threads;
        for (int i = 0; i < 2; ++i) threads.emplace_back([&, i] { ++ready; while (ready < 2) {} results[i] = deleteCacheFile(target); });
        for (auto& thread : threads) thread.join();
        const int removed = (results[0].state == CacheDeleteState::deleted) + (results[1].state == CacheDeleteState::deleted);
        require(removed == 1 && !std::filesystem::exists(file), "Concurrent cleanups deleted a package twice or not at all");
        for (const auto& item : results)
            if (item.state != CacheDeleteState::deleted) {
                require(item.state == CacheDeleteState::absent || item.state == CacheDeleteState::busy || item.state == CacheDeleteState::changed,
                    "The losing concurrent cleanup reported an unexplained state");
                ++other;
            }
        deleted += removed;
    }

    const std::wstring stage = L"0123456789abcdef0123456789abcdef";
    std::filesystem::create_directories(fixture.root / L"Stages" / stage);
    Directory stages(fixture.root / L"Stages"), stageDirectory(fixture.root / L"Stages" / stage);
    const auto worker = fixture.root / L"Stages" / stage / L"nks-update-worker.exe";
    { std::ofstream out(worker, std::ios::binary); out << std::string(8192, 'w'); }
    HANDLE handle = CreateFileW(worker.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    CacheDeleteTarget staged; staged.cacheRoot = cache.value; staged.parent = stageDirectory.value; staged.rootId = cache.id;
    staged.parentId = stageDirectory.id; staged.parentRelative = L"Stages\\" + stage; staged.leaf = L"nks-update-worker.exe";
    staged.size = 8192; staged.hash = hashFile(handle, 8192);
    require(GetFileInformationByHandleEx(handle, FileIdInfo, &staged.id, sizeof(staged.id)), "Fixture stage identity unavailable");
    CloseHandle(handle);
    const auto extra = fixture.root / L"Stages" / stage / L"left.txt";
    { std::ofstream out(extra); out << "x"; }
    const auto removedWorker = deleteCacheFile(staged);
    require(removedWorker.state == CacheDeleteState::deleted, "The staged worker was not deleted");
    CacheDeleteTarget directory; directory.cacheRoot = cache.value; directory.parent = stages.value; directory.rootId = cache.id;
    directory.parentId = stages.id; directory.id = stageDirectory.id; directory.parentRelative = L"Stages"; directory.leaf = stage;
    require(deleteEmptyCacheStage(directory, removedWorker).state == CacheDeleteState::busy && std::filesystem::exists(extra),
        "A stage with an unexpected file was removed");
    std::filesystem::remove(extra);
    stageDirectory.close();
    const auto removedStage = deleteEmptyCacheStage(directory, removedWorker);
    require(removedStage.state == CacheDeleteState::deleted && removedStage.directories == 1 && !std::filesystem::exists(fixture.root / L"Stages" / stage),
        "The emptied stage was not removed");
    std::cout << "PASS real cache deletion: verified, absent, busy, changed content and size, hard link, " << deleted
        << " concurrent rounds with one winner (" << other << " losers absent/busy/changed), partial stage kept then removed\n";
}
// The cache addresses its records by volume GUID path; publishing one must work through that path form.
void volumeRecord()
{
    Fixture fixture;
    Directory root(fixture.root);
    std::wstring canonical(32768, L'\0');
    const DWORD length = GetFinalPathNameByHandleW(root.value, canonical.data(), static_cast<DWORD>(canonical.size()), VOLUME_NAME_GUID | FILE_NAME_NORMALIZED);
    require(length > 0 && length < canonical.size(), "Fixture volume path unavailable");
    canonical.resize(length);
    // Hold the destination exactly as Cache pins its partitions while it publishes records.
    const HANDLE pinned = CreateFileW(canonical.c_str(), FILE_READ_ATTRIBUTES | FILE_TRAVERSE | READ_CONTROL | SYNCHRONIZE, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    require(pinned != INVALID_HANDLE_VALUE, "Fixture directory pin unavailable");
    struct Close { HANDLE value; ~Close() { CloseHandle(value); } } close{pinned};
    const std::byte payload[]{std::byte{1}, std::byte{2}, std::byte{3}};
    try { writeNew(canonical + L"\\record.bin", payload); }
    catch (const Error& error) { std::cout << "DIAG writeNew windows=" << error.windows << ' ' << error.what() << '\n'; throw; }
    require(readData(canonical + L"\\record.bin").size() == 3 && std::filesystem::exists(fixture.root / L"record.bin"),
        "A record published through the volume GUID path is missing");
    bool duplicate = false;
    try { writeNew(canonical + L"\\record.bin", payload); } catch (const Error&) { duplicate = true; }
    require(duplicate, "An existing immutable record was replaced");
    std::cout << "PASS volume GUID record: published, read back, duplicate refused\n";
}
}
int main()
{
    try { verify(); realDeletion(); volumeRecord(); std::cout << "Update cleanup retention checks passed\n"; return 0; }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
