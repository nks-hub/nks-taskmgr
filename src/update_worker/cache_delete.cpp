#include <phnt_windows.h>
#include <phnt.h>
#include "cache_delete.hpp"
#include <aclapi.h>
#include <algorithm>
#include <string_view>

namespace nks::update {
namespace {
struct Failure { CacheDeleteResult result; };
struct LocalMemory { void* value{}; ~LocalMemory() { if (value) LocalFree(value); } };
struct ParentLease {
    broker::Handle root;
    std::vector<broker::Handle> children;
    HANDLE parent{};
    broker::TokenIdentity user;
};

CacheDeleteResult errorResult(DWORD windows, LONG status = 0)
{
    CacheDeleteResult result; result.windows = windows; result.ntStatus = status;
    switch (windows) {
    case ERROR_SHARING_VIOLATION: case ERROR_LOCK_VIOLATION: case ERROR_DELETE_PENDING:
    case ERROR_USER_MAPPED_FILE: case ERROR_DIR_NOT_EMPTY: case ERROR_BUSY:
        result.state = CacheDeleteState::busy; break;
    case ERROR_ACCESS_DENIED: case ERROR_PRIVILEGE_NOT_HELD: case ERROR_WRITE_PROTECT:
        result.state = CacheDeleteState::denied; break;
    case ERROR_FILE_INVALID: case ERROR_INVALID_DATA: case ERROR_REPARSE_TAG_INVALID:
    case ERROR_REPARSE_TAG_MISMATCH: case ERROR_CANT_ACCESS_FILE:
        result.state = CacheDeleteState::changed; break;
    default: break;
    }
    return result;
}
[[noreturn]] void fail(DWORD windows, LONG status = 0)
{
    throw Failure{errorResult(windows ? windows : ERROR_GEN_FAILURE, status)};
}
[[noreturn]] void changed(DWORD windows = ERROR_FILE_INVALID)
{
    auto result = errorResult(windows); result.state = CacheDeleteState::changed; throw Failure{std::move(result)};
}
bool validId(const FILE_ID_INFO& id)
{
    return std::any_of(std::begin(id.FileId.Identifier), std::end(id.FileId.Identifier), [](BYTE byte) { return byte != 0; });
}
bool lowerHex(std::wstring_view value, std::size_t length)
{
    return value.size() == length && std::all_of(value.begin(), value.end(), [](wchar_t c) {
        return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f');
    });
}
bool stageParent(std::wstring_view relative)
{
    return relative.starts_with(L"Stages\\") && lowerHex(relative.substr(7), 32);
}
bool fileNameAllowed(const CacheDeleteTarget& target)
{
    const std::wstring_view name(target.leaf);
    if (target.parentRelative == L"Packages")
        return name.size() == 68 && lowerHex(name.substr(0, 64), 64) && name.substr(64) == L".exe"
            && target.leaf == hex(target.hash) + L".exe";
    if (target.parentRelative == L"Plans")
        return name.size() == 36 && lowerHex(name.substr(0, 32), 32) && name.substr(32) == L".bin";
    if (target.parentRelative == L"Journal")
        return name.size() == 38 && lowerHex(name.substr(0, 32), 32) && name[32] == L'-'
            && name[33] >= L'0' && name[33] <= L'4' && name.substr(34) == L".bin";
    return stageParent(target.parentRelative) && name == L"nks-update-worker.exe";
}
void ownedObject(HANDLE file, const broker::TokenIdentity& user)
{
    if (GetFileType(file) != FILE_TYPE_DISK) changed();
    PSECURITY_DESCRIPTOR descriptor = nullptr; PSID owner = nullptr;
    const DWORD status = GetSecurityInfo(file, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr, nullptr, &descriptor);
    LocalMemory memory{descriptor};
    if (status) fail(status);
    if (!owner || !IsValidSid(owner) || !EqualSid(owner, const_cast<std::byte*>(user.user.data()))) changed(ERROR_INVALID_OWNER);
}
void directoryIdentity(HANDLE directory, const FILE_ID_INFO* expected, const broker::TokenIdentity& user)
{
    FILE_ATTRIBUTE_TAG_INFO attributes{}; FILE_ID_INFO id{}; FILE_STANDARD_INFO standard{};
    if (!GetFileInformationByHandleEx(directory, FileAttributeTagInfo, &attributes, sizeof(attributes))
        || !GetFileInformationByHandleEx(directory, FileIdInfo, &id, sizeof(id))
        || !GetFileInformationByHandleEx(directory, FileStandardInfo, &standard, sizeof(standard))) fail(GetLastError());
    if ((attributes.FileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) != FILE_ATTRIBUTE_DIRECTORY
        || !standard.Directory || standard.DeletePending || !validId(id) || (expected && !equalId(id, *expected))) changed();
    ownedObject(directory, user);
}
broker::Handle openRelative(HANDLE parent, std::wstring_view leaf, ACCESS_MASK access, bool directory, bool& absent)
{
    absent = false;
    UNICODE_STRING name{}; name.Buffer = const_cast<PWSTR>(leaf.data());
    name.Length = name.MaximumLength = static_cast<USHORT>(leaf.size() * sizeof(wchar_t));
    OBJECT_ATTRIBUTES attributes{};
    InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE, parent, nullptr);
    IO_STATUS_BLOCK io{}; HANDLE raw = nullptr;
    const ULONG sharing = (access & DELETE) ? 0 : FILE_SHARE_READ;
    const auto status = NtCreateFile(&raw, access | SYNCHRONIZE, &attributes, &io, nullptr, 0, sharing,
        // NtCreateFile rejects FILE_OPEN_NO_RECALL together with FILE_DIRECTORY_FILE (STATUS_INVALID_PARAMETER).
        FILE_OPEN, (directory ? FILE_DIRECTORY_FILE : FILE_NON_DIRECTORY_FILE | FILE_OPEN_NO_RECALL) | FILE_OPEN_REPARSE_POINT
        | FILE_SYNCHRONOUS_IO_NONALERT, nullptr, 0);
    if (status == STATUS_OBJECT_NAME_NOT_FOUND || status == STATUS_NO_SUCH_FILE) { absent = true; return {}; }
    if (!NT_SUCCESS(status)) fail(RtlNtStatusToDosError(status), status);
    return broker::Handle(raw);
}
ParentLease parentLease(const CacheDeleteTarget& target)
{
    if (!target.cacheRoot || target.cacheRoot == INVALID_HANDLE_VALUE || !target.parent || target.parent == INVALID_HANDLE_VALUE
        || !validId(target.rootId) || !validId(target.parentId) || !validId(target.id)) changed(ERROR_INVALID_PARAMETER);
    ParentLease held;
    if (!broker::readToken(GetCurrentProcess(), held.user)) fail(GetLastError());
    if (held.user.user.empty() || !IsValidSid(held.user.user.data()) || held.user.elevated
        || held.user.integrity >= SECURITY_MANDATORY_HIGH_RID) fail(ERROR_ACCESS_DENIED);
    // ReOpenFile refuses directory handles with ERROR_ACCESS_DENIED; a duplicate names the same verified directory object.
    HANDLE root = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), target.cacheRoot, GetCurrentProcess(), &root,
        FILE_READ_ATTRIBUTES | FILE_TRAVERSE | READ_CONTROL | SYNCHRONIZE, FALSE, 0)) fail(GetLastError());
    held.root.reset(root);
    directoryIdentity(held.root.get(), &target.rootId, held.user);
    directoryIdentity(target.parent, &target.parentId, held.user);
    HANDLE current = held.root.get(); std::size_t at = 0;
    for (;;) {
        const auto slash = target.parentRelative.find(L'\\', at);
        const std::wstring_view segment(target.parentRelative.data() + at,
            (slash == std::wstring::npos ? target.parentRelative.size() : slash) - at);
        bool absent = false;
        auto child = openRelative(current, segment, FILE_READ_ATTRIBUTES | READ_CONTROL, true, absent);
        if (absent) changed(ERROR_PATH_NOT_FOUND);
        directoryIdentity(child.get(), nullptr, held.user);
        current = child.get(); held.children.push_back(std::move(child));
        if (slash == std::wstring::npos) break;
        at = slash + 1;
    }
    held.parent = current;
    directoryIdentity(held.parent, &target.parentId, held.user);
    return held;
}
void fileIdentity(HANDLE file, const CacheDeleteTarget& target, const broker::TokenIdentity& user)
{
    FILE_ATTRIBUTE_TAG_INFO attributes{}; FILE_ID_INFO id{}; FILE_STANDARD_INFO standard{};
    if (!GetFileInformationByHandleEx(file, FileAttributeTagInfo, &attributes, sizeof(attributes))
        || !GetFileInformationByHandleEx(file, FileIdInfo, &id, sizeof(id))
        || !GetFileInformationByHandleEx(file, FileStandardInfo, &standard, sizeof(standard))) fail(GetLastError());
    if ((attributes.FileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DEVICE))
        || standard.Directory || standard.DeletePending || standard.NumberOfLinks != 1 || standard.EndOfFile.QuadPart < 0
        || static_cast<std::uint64_t>(standard.EndOfFile.QuadPart) != target.size || !equalId(id, target.id)) changed();
    ownedObject(file, user);
}
CacheDeleteResult disposition(broker::Handle file, HANDLE parent, std::wstring_view leaf, bool directory)
{
    FILE_DISPOSITION_INFO request{}; request.DeleteFile = TRUE;
    if (!SetFileInformationByHandle(file.get(), FileDispositionInfo, &request, sizeof(request))) fail(GetLastError());
    const HANDLE closing = file.release();
    if (!CloseHandle(closing)) fail(GetLastError());
    // Delete-pending is not proof that the last other handle has closed.
    bool absent = false;
    auto remaining = openRelative(parent, leaf, FILE_READ_ATTRIBUTES, directory, absent);
    if (!absent) changed(ERROR_FILE_EXISTS);
    CacheDeleteResult result; result.state = CacheDeleteState::deleted; return result;
}
}

CacheDeleteResult deleteCacheFile(const CacheDeleteTarget& target)
{
    try {
        if (!fileNameAllowed(target) || !target.size || target.size > maxPackageSize || empty(target.hash)) changed(ERROR_INVALID_PARAMETER);
        auto held = parentLease(target); bool absent = false;
        auto withStageReceipt = [&](CacheDeleteResult result) {
            if (stageParent(target.parentRelative)) {
                result.stageRoot = target.rootId; result.stageId = target.parentId; result.stageLeaf = target.parentRelative.substr(7);
            }
            return result;
        };
        auto file = openRelative(held.parent, target.leaf, DELETE | GENERIC_READ | READ_CONTROL, false, absent);
        if (absent) { CacheDeleteResult result; result.state = CacheDeleteState::absent; return withStageReceipt(std::move(result)); }
        fileIdentity(file.get(), target, held.user);
        if (hashFile(file.get(), target.size) != target.hash) changed(ERROR_INVALID_DATA);
        fileIdentity(file.get(), target, held.user);
        directoryIdentity(held.root.get(), &target.rootId, held.user);
        directoryIdentity(held.parent, &target.parentId, held.user);
        auto result = disposition(std::move(file), held.parent, target.leaf, false);
        result.files = 1; result.bytes = target.size;
        return withStageReceipt(std::move(result));
    } catch (const Failure& failure) { return failure.result; }
    catch (const Error& error) { return errorResult(error.windows ? error.windows : ERROR_GEN_FAILURE); }
}

CacheDeleteResult deleteEmptyCacheStage(const CacheDeleteTarget& target, const CacheDeleteResult& removedFile)
{
    try {
        const bool verifiedChild = (removedFile.state == CacheDeleteState::deleted && removedFile.files == 1)
            || (removedFile.state == CacheDeleteState::absent && !removedFile.files && !removedFile.bytes);
        if (target.parentRelative != L"Stages" || !lowerHex(target.leaf, 32) || target.size || !empty(target.hash)
            || !verifiedChild || removedFile.directories || removedFile.stageLeaf != target.leaf
            || !equalId(removedFile.stageRoot, target.rootId) || !equalId(removedFile.stageId, target.id)) changed(ERROR_INVALID_PARAMETER);
        auto held = parentLease(target); bool absent = false;
        auto directory = openRelative(held.parent, target.leaf, DELETE | FILE_READ_ATTRIBUTES | READ_CONTROL, true, absent);
        if (absent) { CacheDeleteResult result; result.state = CacheDeleteState::absent; return result; }
        directoryIdentity(directory.get(), &target.id, held.user);
        directoryIdentity(held.root.get(), &target.rootId, held.user);
        directoryIdentity(held.parent, &target.parentId, held.user);
        auto result = disposition(std::move(directory), held.parent, target.leaf, true);
        result.directories = 1; return result;
    } catch (const Failure& failure) { return failure.result; }
}
}
