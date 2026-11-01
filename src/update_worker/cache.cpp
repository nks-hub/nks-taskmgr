#include "cache.hpp"
#include <shlobj.h>
#include <sddl.h>
#include <aclapi.h>
#include <algorithm>
#include <cstring>

namespace nks::update {
namespace {
struct Local { void* value{}; ~Local() { if (value) LocalFree(value); } };
bool isHashName(const std::wstring& name)
{
    return name.size() == 68 && name.substr(64) == L".exe" && std::all_of(name.begin(), name.begin() + 64,
        [](wchar_t c) { return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'); });
}
bool isPlanName(const std::wstring& name)
{
    return name.size() == 32 && std::all_of(name.begin(), name.end(), [](wchar_t c) { return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'); });
}
broker::Handle directory(const std::wstring& path)
{
    broker::Handle result(CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES | READ_CONTROL, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    FILE_ATTRIBUTE_TAG_INFO attributes{};
    if (!result || result.get() == INVALID_HANDLE_VALUE || !GetFileInformationByHandleEx(result.get(), FileAttributeTagInfo, &attributes, sizeof(attributes))
        || !(attributes.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) || (attributes.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT))
        throw Error(Status::rejected, "Cache directory is inaccessible or redirected", GetLastError());
    return result;
}
void secureDirectory(const std::wstring& path, SECURITY_ATTRIBUTES& security, const broker::TokenIdentity& user)
{
    if (!CreateDirectoryW(path.c_str(), &security) && GetLastError() != ERROR_ALREADY_EXISTS) throw Error(Status::io, "Cannot create private cache directory", GetLastError());
    auto handle = directory(path); PSECURITY_DESCRIPTOR descriptor = nullptr; PSID owner = nullptr; PACL acl = nullptr;
    const DWORD status = GetSecurityInfo(handle.get(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
        &owner, nullptr, &acl, nullptr, &descriptor); Local memory{descriptor};
    SECURITY_DESCRIPTOR_CONTROL control = 0; DWORD revision = 0;
    if (status || !owner || !EqualSid(owner, const_cast<std::byte*>(user.user.data())) || !acl
        || !GetSecurityDescriptorControl(descriptor, &control, &revision) || !(control & SE_DACL_PROTECTED))
        throw Error(Status::rejected, "Cache directory is not privately owned", status);
    bool granted = false;
    for (WORD i = 0; i < acl->AceCount; ++i) {
        void* raw = nullptr;
        if (!GetAce(acl, i, &raw)) throw Error(Status::rejected, "Invalid cache access list");
        const auto* ace = static_cast<const ACCESS_ALLOWED_ACE*>(raw);
        if (ace->Header.AceType != ACCESS_ALLOWED_ACE_TYPE || !EqualSid(const_cast<DWORD*>(&ace->SidStart), const_cast<std::byte*>(user.user.data())))
            throw Error(Status::rejected, "Unexpected cache access principal");
        granted = granted || (ace->Mask & FILE_ALL_ACCESS) == FILE_ALL_ACCESS;
    }
    if (!granted) throw Error(Status::rejected, "Incomplete cache access rights");
}
std::uint64_t fileSize(const std::wstring& path)
{
    broker::Handle file(CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    FILE_ATTRIBUTE_TAG_INFO attributes{}; LARGE_INTEGER size{};
    if (!file || file.get() == INVALID_HANDLE_VALUE || !GetFileInformationByHandleEx(file.get(), FileAttributeTagInfo, &attributes, sizeof(attributes))
        || (attributes.FileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) || !GetFileSizeEx(file.get(), &size) || size.QuadPart < 0)
        throw Error(Status::unknown, "Unrecognized cache file", GetLastError());
    return static_cast<std::uint64_t>(size.QuadPart);
}
Image copyImage(Image& source, const std::wstring& destination, Role role)
{
    broker::Handle writer(CreateFileW(destination.c_str(), GENERIC_READ | GENERIC_WRITE | FILE_EXECUTE, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!writer || writer.get() == INVALID_HANDLE_VALUE) throw Error(Status::io, "Cannot reserve a new cache file", GetLastError());
    FILE_ID_INFO id{};
    if (!GetFileInformationByHandleEx(writer.get(), FileIdInfo, &id, sizeof(id))) throw Error(Status::io, "Cannot identify reserved cache file", GetLastError());
    LARGE_INTEGER start{};
    if (!SetFilePointerEx(source.lease.image.get(), start, nullptr, FILE_BEGIN)) throw Error(Status::io, "Cannot rewind image", GetLastError());
    std::array<std::byte, 65536> bytes{}; std::uint64_t total = 0;
    for (;;) {
        DWORD count = 0;
        if (!ReadFile(source.lease.image.get(), bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr)) throw Error(Status::io, "Cannot read cache source", GetLastError());
        if (!count) break;
        total += count; if (total > source.size) throw Error(Status::rejected, "Cache source changed");
        DWORD written = 0;
        if (!WriteFile(writer.get(), bytes.data(), count, &written, nullptr) || written != count) throw Error(Status::io, "Cannot write cache image", GetLastError());
    }
    if (total != source.size || !FlushFileBuffers(writer.get())) throw Error(Status::io, "Cache copy was not durably completed", GetLastError());
    // Keep the file object pinned across the transition from a write handle to an immutable read handle.
    broker::Handle transfer(CreateFileW(destination.c_str(), GENERIC_READ | FILE_EXECUTE, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr));
    if (!transfer || transfer.get() == INVALID_HANDLE_VALUE) throw Error(Status::io, "Cannot pin copied image", GetLastError());
    writer.reset(); auto result = openImage(destination, role);
    if (!equalId(result.lease.fileId, id) || result.size != source.size || result.hash != source.hash || !sameVersion(result, source))
        throw Error(Status::rejected, "Copied image differs from the verified source");
    return result;
}
}

std::vector<std::wstring> directoryEntries(const std::wstring& path, std::size_t limit)
{
    WIN32_FIND_DATAW entry{}; const HANDLE raw = FindFirstFileW((path + L"\\*").c_str(), &entry);
    if (raw == INVALID_HANDLE_VALUE) {
        if (GetLastError() == ERROR_FILE_NOT_FOUND) return {};
        throw Error(Status::io, "Cannot enumerate cache", GetLastError());
    }
    struct Find { HANDLE value; ~Find() { FindClose(value); } } find{raw}; std::vector<std::wstring> result;
    do {
        const std::wstring name(entry.cFileName); if (name == L"." || name == L"..") continue;
        if (entry.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) throw Error(Status::unknown, "Cache contains a redirected entry");
        if (result.size() == limit) throw Error(Status::cacheFull, "Cache entry limit reached");
        result.push_back(name);
    } while (FindNextFileW(raw, &entry));
    if (GetLastError() != ERROR_NO_MORE_FILES) throw Error(Status::io, "Cache enumeration is incomplete", GetLastError());
    return result;
}

Cache::Cache()
{
    broker::TokenIdentity token;
    if (!broker::trustConfigured()) throw Error(Status::trustMissing, "Update publisher trust is not configured", broker::trustNotConfigured);
    if (!broker::readToken(GetCurrentProcess(), token) || token.elevated || token.integrity >= SECURITY_MANDATORY_HIGH_RID)
        throw Error(Status::rejected, "Updater requires an ordinary interactive user");
    wchar_t* sid = nullptr;
    if (!ConvertSidToStringSidW(token.user.data(), &sid)) throw Error(Status::io, "Cannot identify cache owner", GetLastError());
    Local sidMemory{sid};
    mutex_.reset(CreateMutexW(nullptr, FALSE, (L"Global\\NksTaskMgr-Update-" + std::wstring(sid)).c_str()));
    if (!mutex_ || GetLastError() == ERROR_ALREADY_EXISTS) throw Error(Status::busy, "Another update worker is active");
    wchar_t* known = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DONT_VERIFY, nullptr, &known))) throw Error(Status::io, "LocalAppData is unavailable");
    std::wstring base(known); CoTaskMemFree(known);
    if (base.size() < 3 || base[1] != L':' || base[2] != L'\\') throw Error(Status::rejected, "LocalAppData is not a local absolute path");
    for (std::size_t end = 3;;) {
        directories_.push_back(directory(base.substr(0, end)));
        if (end == base.size()) break;
        const auto slash = base.find(L'\\', end + 1);
        end = slash == std::wstring::npos ? base.size() : slash;
    }
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    const std::wstring dacl = L"D:P(A;OICI;FA;;;" + std::wstring(sid) + L")";
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(dacl.c_str(), SDDL_REVISION_1, &descriptor, nullptr)) throw Error(Status::io, "Cannot construct private cache access", GetLastError());
    Local acl{descriptor}; SECURITY_ATTRIBUTES security{sizeof(security), descriptor, FALSE};
    base += L"\\NksTaskMgr";
    if (!CreateDirectoryW(base.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS) throw Error(Status::io, "Cannot create product data directory", GetLastError());
    directories_.push_back(directory(base));
    root_ = base + L"\\Updates"; secureDirectory(root_, security, token);
    auto rootHandle = directory(root_); std::wstring canonical(32768, L'\0');
    const DWORD length = GetFinalPathNameByHandleW(rootHandle.get(), canonical.data(), static_cast<DWORD>(canonical.size()), VOLUME_NAME_GUID | FILE_NAME_NORMALIZED);
    if (!length || length >= canonical.size()) throw Error(Status::rejected, "Cannot canonicalize cache path", GetLastError());
    canonical.resize(length); root_ = std::move(canonical);
    const auto volumeEnd = root_.find(L"}\\");
    if (!root_.starts_with(L"\\\\?\\Volume{") || volumeEnd == std::wstring::npos || GetDriveTypeW(root_.substr(0, volumeEnd + 2).c_str()) != DRIVE_FIXED)
        throw Error(Status::rejected, "Cache is not on a local fixed volume");
    for (auto slash = volumeEnd + 1; slash != std::wstring::npos; slash = root_.find(L'\\', slash + 1))
        directories_.push_back(directory(root_.substr(0, slash == volumeEnd + 1 ? slash + 1 : slash)));
    directories_.push_back(std::move(rootHandle));
    for (const wchar_t* child : {L"Packages", L"Plans", L"Stages", L"Journal"}) {
        const auto path = root_ + L"\\" + child; secureDirectory(path, security, token); directories_.push_back(directory(path));
    }
}
std::wstring Cache::packagePath(const Hash& hash) const { return root_ + L"\\Packages\\" + hex(hash) + L".exe"; }
std::wstring Cache::stagePath(const PlanId& id) const { return root_ + L"\\Stages\\" + hex(id) + L"\\nks-update-worker.exe"; }
std::wstring Cache::planPath(const PlanId& id) const { return root_ + L"\\Plans\\" + hex(id) + L".bin"; }
std::wstring Cache::journalPath(const PlanId& id, unsigned step) const { return root_ + L"\\Journal\\" + hex(id) + L"-" + std::to_wstring(step) + L".bin"; }
std::vector<Image> Cache::packages() const
{
    std::vector<Image> result;
    for (const auto& name : directoryEntries(root_ + L"\\Packages", maxPackages)) {
        if (!isHashName(name)) throw Error(Status::unknown, "Cache contains an unrecognized package file");
        auto image = openImage(root_ + L"\\Packages\\" + name, Role::installer);
        if (name != hex(image.hash) + L".exe") throw Error(Status::unknown, "Cached package hash is inconsistent");
        result.push_back(std::move(image));
    }
    return result;
}
std::uint64_t Cache::usage() const
{
    std::uint64_t total = 1048576;
    for (const auto& name : directoryEntries(root_ + L"\\Packages", maxPackages)) {
        if (!isHashName(name)) throw Error(Status::unknown, "Unrecognized cache package");
        total += fileSize(root_ + L"\\Packages\\" + name);
    }
    for (const auto& name : directoryEntries(root_ + L"\\Stages", maxPlans)) {
        if (!isPlanName(name)) throw Error(Status::unknown, "Unrecognized staging directory");
        const auto files = directoryEntries(root_ + L"\\Stages\\" + name, 1);
        if (files.empty()) continue;
        if (files[0] != L"nks-update-worker.exe") throw Error(Status::unknown, "Unrecognized staging file");
        total += fileSize(root_ + L"\\Stages\\" + name + L"\\" + files[0]);
    }
    if (total > maxCacheSize) throw Error(Status::cacheFull, "Update cache size limit reached");
    return total;
}
Image Cache::store(Image& source)
{
    const auto destination = packagePath(source.hash);
    if (GetFileAttributesW(destination.c_str()) != INVALID_FILE_ATTRIBUTES) {
        auto existing = openImage(destination, Role::installer);
        if (existing.hash != source.hash || existing.size != source.size || !sameVersion(existing, source)) throw Error(Status::unknown, "Existing cache entry is inconsistent");
        return existing;
    }
    if (directoryEntries(root_ + L"\\Packages", maxPackages).size() >= maxPackages || usage() + source.size > maxCacheSize)
        throw Error(Status::cacheFull, "Update cache is full");
    return copyImage(source, destination, Role::installer);
}
Image Cache::stage(Image& self, const PlanId& id)
{
    if (self.size > 64ull * 1024 * 1024 || directoryEntries(root_ + L"\\Stages", maxPlans).size() >= maxPlans
        || directoryEntries(root_ + L"\\Plans", maxPlans).size() >= maxPlans || usage() + self.size > maxCacheSize)
        throw Error(Status::cacheFull, "Update plan or staging limit reached");
    const auto destination = stagePath(id); const auto folder = parentPath(destination);
    if (!CreateDirectoryW(folder.c_str(), nullptr)) throw Error(Status::io, "Cannot reserve staging directory", GetLastError());
    auto held = directory(folder); return copyImage(self, destination, Role::worker);
}
std::vector<std::wstring> Cache::journalFiles() const { return directoryEntries(root_ + L"\\Journal", maxJournalFiles); }
std::vector<std::byte> readData(const std::wstring& path, std::size_t limit)
{
    broker::Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    FILE_ATTRIBUTE_TAG_INFO attributes{}; LARGE_INTEGER size{};
    if (!file || file.get() == INVALID_HANDLE_VALUE || !GetFileInformationByHandleEx(file.get(), FileAttributeTagInfo, &attributes, sizeof(attributes))
        || (attributes.FileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) || !GetFileSizeEx(file.get(), &size)
        || size.QuadPart <= 0 || static_cast<std::uint64_t>(size.QuadPart) > limit) throw Error(Status::unknown, "Invalid update record", GetLastError());
    std::vector<std::byte> bytes(static_cast<std::size_t>(size.QuadPart)); DWORD count = 0;
    if (!ReadFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr) || count != bytes.size()) throw Error(Status::unknown, "Truncated update record", GetLastError());
    return bytes;
}
void writeNew(const std::wstring& path, std::span<const std::byte> bytes)
{
    if (bytes.empty() || bytes.size() > maxPayload) throw Error(Status::invalidRequest, "Update record exceeds the limit");
    broker::Handle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_FLAG_WRITE_THROUGH, nullptr));
    if (!file || file.get() == INVALID_HANDLE_VALUE) throw Error(Status::conflict, "Cannot reserve immutable update record", GetLastError());
    DWORD count = 0;
    if (!WriteFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr) || count != bytes.size() || !FlushFileBuffers(file.get()))
        throw Error(Status::unknown, "Update journal write is incomplete", GetLastError());
}
}
