#include "update_image.hpp"
#include <bcrypt.h>
#include <algorithm>
#include <chrono>
#include <cwctype>
#include <cstring>

namespace nks::update {
std::wstring hex(std::span<const std::byte> bytes)
{
    constexpr wchar_t digits[] = L"0123456789abcdef"; std::wstring result; result.reserve(bytes.size() * 2);
    for (auto value : bytes) { const auto number = std::to_integer<unsigned>(value); result += digits[number >> 4]; result += digits[number & 15]; }
    return result;
}
bool equalId(const FILE_ID_INFO& a, const FILE_ID_INFO& b) noexcept
{
    return a.VolumeSerialNumber == b.VolumeSerialNumber && !std::memcmp(a.FileId.Identifier, b.FileId.Identifier, sizeof(a.FileId.Identifier));
}
std::wstring parentPath(const std::wstring& path)
{
    const auto slash = path.find_last_of(L'\\');
    if (slash == std::wstring::npos) throw Error(Status::rejected, "Image path has no parent directory");
    return path.substr(0, slash);
}
std::uint64_t unixMs()
{
    FILETIME now{}; GetSystemTimeAsFileTime(&now);
    return (((static_cast<std::uint64_t>(now.dwHighDateTime) << 32) | now.dwLowDateTime) - 116444736000000000ull) / 10000;
}
Hash hashFile(HANDLE file, std::uint64_t limit)
{
    LARGE_INTEGER size{}, start{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || static_cast<std::uint64_t>(size.QuadPart) > limit
        || !SetFilePointerEx(file, start, nullptr, FILE_BEGIN)) throw Error(Status::rejected, "Image size is outside the allowed limit", GetLastError());
    BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_HASH_HANDLE digest = nullptr;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) throw Error(Status::io, "SHA-256 is unavailable");
    try {
        if (BCryptCreateHash(algorithm, &digest, nullptr, 0, nullptr, 0, 0) != 0) throw Error(Status::io, "SHA-256 initialization failed");
        std::array<BYTE, 65536> bytes{}; std::uint64_t total = 0;
        for (;;) {
            DWORD count = 0;
            if (!ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr)) throw Error(Status::io, "Cannot read image", GetLastError());
            if (!count) break;
            total += count;
            if (total > limit || BCryptHashData(digest, bytes.data(), count, 0) != 0) throw Error(Status::io, "Cannot hash image");
        }
        Hash result{};
        if (total != static_cast<std::uint64_t>(size.QuadPart)
            || BCryptFinishHash(digest, reinterpret_cast<PUCHAR>(result.data()), static_cast<ULONG>(result.size()), 0) != 0) throw Error(Status::rejected, "Image changed while hashing");
        BCryptDestroyHash(digest); BCryptCloseAlgorithmProvider(algorithm, 0); return result;
    } catch (...) { if (digest) BCryptDestroyHash(digest); BCryptCloseAlgorithmProvider(algorithm, 0); throw; }
}

void verifyVersion(Image& image, Role role)
{
    DWORD ignored = 0; const DWORD length = GetFileVersionInfoSizeExW(FILE_VER_GET_NEUTRAL, image.lease.path.c_str(), &ignored);
    if (!length || length > 1048576) throw Error(Status::rejected, "Missing or oversized image version resource");
    std::vector<std::byte> bytes(length);
    if (!GetFileVersionInfoExW(FILE_VER_GET_NEUTRAL, image.lease.path.c_str(), 0, length, bytes.data())) throw Error(Status::rejected, "Cannot read image version resource");
    struct Translation { WORD language, page; };
    Translation* translations = nullptr; UINT translated = 0;
    if (!VerQueryValueW(bytes.data(), L"\\VarFileInfo\\Translation", reinterpret_cast<void**>(&translations), &translated)
        || !translated || translated % sizeof(Translation) || translated / sizeof(Translation) > 32) throw Error(Status::rejected, "Missing image language table");
    const wchar_t* description = role == Role::installer ? L"NKS Task Manager Setup" : role == Role::worker ? L"NKS Task Manager Update Worker" : L"NKS Task Manager";
    const wchar_t* filename = role == Role::installer ? L"NksTaskMgrSetup.exe" : role == Role::worker ? L"nks-update-worker.exe" : L"NksTaskMgr.WinUI.dll";
    std::wstring version;
    for (UINT i = 0; i < translated / sizeof(Translation); ++i) {
        auto field = [&](const wchar_t* name) {
            wchar_t query[128]{}; swprintf_s(query, L"\\StringFileInfo\\%04x%04x\\%ls", translations[i].language, translations[i].page, name);
            wchar_t* value = nullptr; UINT size = 0;
            if (!VerQueryValueW(bytes.data(), query, reinterpret_cast<void**>(&value), &size) || !value || size < 1 || size > 4096 || value[size - 1])
                throw Error(Status::rejected, "Incomplete image role resource");
            std::wstring result(value, size - 1);
            if (result.find(L'\0') != std::wstring::npos) throw Error(Status::rejected, "Embedded null in image role");
            return result;
        };
        if (field(L"ProductName") != L"NKS Task Manager" || field(L"FileDescription") != description || field(L"OriginalFilename") != filename)
            throw Error(Status::rejected, "Signed image has the wrong product role");
        const auto current = field(L"ProductVersion");
        if (!version.empty() && current != version) throw Error(Status::rejected, "Conflicting image versions");
        version = current;
    }
    const auto plus = version.find(L'+');
    if (plus == std::wstring::npos || version.size() - plus - 1 != 40) throw Error(Status::rejected, "Image version lacks a full revision");
    std::size_t at = 0;
    for (std::size_t part = 0; part < 3; ++part) {
        const auto end = part == 2 ? plus : version.find(L'.', at);
        if (end == std::wstring::npos || end <= at || end - at > 5 || (end - at > 1 && version[at] == L'0')) throw Error(Status::rejected, "Invalid three-part image version");
        std::uint32_t value = 0;
        for (; at < end; ++at) { const auto c = version[at]; if (c < L'0' || c > L'9') throw Error(Status::rejected, "Invalid image version digit"); value = value * 10 + c - L'0'; }
        if (value > 65535) throw Error(Status::rejected, "Image version component is too large");
        image.numbers[part] = value; at = end + 1;
    }
    image.revision = version.substr(plus + 1);
    for (auto& c : image.revision) { if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') || (c >= L'A' && c <= L'F'))) throw Error(Status::rejected, "Invalid image revision"); c = static_cast<wchar_t>(towlower(c)); }
    image.version = version.substr(0, plus + 1) + image.revision;
}
bool sameVersion(const Image& a, const Image& b) noexcept { return a.version == b.version; }
Image inspectImage(broker::Handle file, Role role)
{
    if (!broker::trustConfigured()) throw Error(Status::trustMissing, "Update publisher trust is not configured", broker::trustNotConfigured);
    Image result; result.lease.image = std::move(file);
    if (!broker::verifyImageFile(result.lease)) throw Error(Status::rejected, "Cannot verify immutable signed image", GetLastError());
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(result.lease.image.get(), &size) || size.QuadPart <= 0 || static_cast<std::uint64_t>(size.QuadPart) > maxPackageSize)
        throw Error(Status::rejected, "Invalid image size", GetLastError());
    result.size = static_cast<std::uint64_t>(size.QuadPart); result.hash = hashFile(result.lease.image.get(), maxPackageSize);
    verifyVersion(result, role); return result;
}
Image openImage(const std::wstring& path, Role role)
{
    broker::Handle file(CreateFileW(path.c_str(), GENERIC_READ | FILE_EXECUTE | SYNCHRONIZE, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    FILE_ATTRIBUTE_TAG_INFO attributes{};
    if (!file || file.get() == INVALID_HANDLE_VALUE || !GetFileInformationByHandleEx(file.get(), FileAttributeTagInfo, &attributes, sizeof(attributes))
        || (attributes.FileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY))) throw Error(Status::rejected, "Image is inaccessible or redirected", GetLastError());
    return inspectImage(std::move(file), role);
}
}
