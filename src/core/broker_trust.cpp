#include <phnt_windows.h>
#include <phnt.h>
#include "broker_trust.hpp"
#include <wintrust.h>
#include <softpub.h>
#include <bcrypt.h>
#include <algorithm>
#include <cstring>
#include <string_view>

#ifndef NKS_BROKER_PUBLISHER_SPKI_SHA256
#define NKS_BROKER_PUBLISHER_SPKI_SHA256 ""
#endif

namespace nks::broker {
namespace {
constexpr std::string_view publisherPin = NKS_BROKER_PUBLISHER_SPKI_SHA256;
int hex(char value) noexcept
{
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    return -1;
}
bool copySid(PSID sid, std::vector<std::byte>& output)
{
    if (!sid || !IsValidSid(sid)) return false;
    const auto size = GetLengthSid(sid);
    if (!size || size > SECURITY_MAX_SID_SIZE) return false;
    output.resize(size); return CopySid(size, output.data(), sid) != FALSE;
}
bool equalSid(const std::vector<std::byte>& a, const std::vector<std::byte>& b) noexcept
{
    return !a.empty() && a.size() == b.size() && EqualSid(const_cast<std::byte*>(a.data()), const_cast<std::byte*>(b.data()));
}
bool tokenData(HANDLE token, TOKEN_INFORMATION_CLASS kind, std::vector<std::byte>& bytes)
{
    DWORD size = 0;
    GetTokenInformation(token, kind, nullptr, 0, &size);
    if (!size || size > 65536) return false;
    bytes.resize(size);
    return GetTokenInformation(token, kind, bytes.data(), size, &size) != FALSE;
}
bool lockPath(Peer& peer)
{
    std::wstring path(32768, L'\0');
    const DWORD size = GetFinalPathNameByHandleW(peer.image.get(), path.data(), static_cast<DWORD>(path.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_GUID);
    if (!size || size >= path.size()) return false;
    path.resize(size);
    if (!path.starts_with(L"\\\\?\\Volume{")) return false;
    auto slash = path.find(L"}\\"); if (slash == std::wstring::npos) return false;
    ++slash;
    const auto rootEnd = slash;
    if (GetDriveTypeW(path.substr(0, rootEnd + 1).c_str()) != DRIVE_FIXED) return false;
    while (slash != std::wstring::npos) {
        const auto directory = path.substr(0, slash == rootEnd ? slash + 1 : slash);
        Handle handle(CreateFileW(directory.c_str(), FILE_READ_ATTRIBUTES | FILE_TRAVERSE, FILE_SHARE_READ, nullptr,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        FILE_ATTRIBUTE_TAG_INFO attributes{};
        if (!handle || handle.get() == INVALID_HANDLE_VALUE
            || !GetFileInformationByHandleEx(handle.get(), FileAttributeTagInfo, &attributes, sizeof(attributes))
            || !(attributes.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) || (attributes.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) return false;
        peer.directories.push_back(std::move(handle)); slash = path.find(L'\\', slash + 1);
    }
    Handle reopened(CreateFileW(path.c_str(), GENERIC_READ | FILE_EXECUTE | SYNCHRONIZE, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr));
    FILE_ID_INFO id{};
    if (!reopened || reopened.get() == INVALID_HANDLE_VALUE || !GetFileInformationByHandleEx(reopened.get(), FileIdInfo, &id, sizeof(id))
        || id.VolumeSerialNumber != peer.fileId.VolumeSerialNumber
        || std::memcmp(id.FileId.Identifier, peer.fileId.FileId.Identifier, sizeof(id.FileId.Identifier))) return false;
    peer.path = std::move(path); return true;
}
bool signerPinned(HANDLE file, const std::wstring& path)
{
    if (!trustConfigured()) return false;
    WINTRUST_FILE_INFO info{sizeof(info), path.c_str(), file, nullptr};
    WINTRUST_DATA data{}; data.cbStruct = sizeof(data); data.dwUIChoice = WTD_UI_NONE;
    data.fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN; data.dwUnionChoice = WTD_CHOICE_FILE;
    data.pFile = &info; data.dwStateAction = WTD_STATEACTION_VERIFY;
    data.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT;
    GUID policy = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    bool valid = false;
    const LONG status = WinVerifyTrust(nullptr, &policy, &data);
    if (status == ERROR_SUCCESS) {
        const auto provider = WTHelperProvDataFromStateData(data.hWVTStateData);
        const auto signer = provider ? WTHelperGetProvSignerFromChain(provider, 0, FALSE, 0) : nullptr;
        const auto cert = signer && signer->csCertChain ? signer->pasCertChain[0].pCert : nullptr;
        if (cert) {
            BYTE* encoded = nullptr; DWORD length = 0;
            if (CryptEncodeObjectEx(X509_ASN_ENCODING, X509_PUBLIC_KEY_INFO, &cert->pCertInfo->SubjectPublicKeyInfo,
                CRYPT_ENCODE_ALLOC_FLAG, nullptr, &encoded, &length)) {
                std::array<BYTE, 32> digest{}, expected{};
                for (std::size_t i = 0; i < expected.size(); ++i)
                    expected[i] = static_cast<BYTE>((hex(publisherPin[i * 2]) << 4) | hex(publisherPin[i * 2 + 1]));
                BCRYPT_ALG_HANDLE algorithm = nullptr;
                if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0) {
                    valid = BCryptHash(algorithm, nullptr, 0, encoded, length, digest.data(), static_cast<ULONG>(digest.size())) == 0;
                    BCryptCloseAlgorithmProvider(algorithm, 0);
                }
                valid = valid && digest == expected; LocalFree(encoded);
            }
        }
    }
    data.dwStateAction = WTD_STATEACTION_CLOSE; WinVerifyTrust(nullptr, &policy, &data);
    return valid;
}
}

bool trustConfigured() noexcept
{
    return publisherPin.size() == 64 && std::all_of(publisherPin.begin(), publisherPin.end(), [](char c) { return hex(c) >= 0; });
}

bool readToken(HANDLE process, TokenIdentity& result)
{
    HANDLE raw = nullptr;
    if (!OpenProcessToken(process, TOKEN_QUERY, &raw)) return false;
    Handle token(raw); std::vector<std::byte> bytes;
    if (!tokenData(raw, TokenUser, bytes) || !copySid(reinterpret_cast<const TOKEN_USER*>(bytes.data())->User.Sid, result.user)) return false;
    if (!tokenData(raw, TokenLogonSid, bytes)) return false;
    const auto groups = reinterpret_cast<const TOKEN_GROUPS*>(bytes.data());
    if (groups->GroupCount != 1 || !(groups->Groups[0].Attributes & SE_GROUP_LOGON_ID)
        || !copySid(groups->Groups[0].Sid, result.logon)) return false;
    if (!tokenData(raw, TokenIntegrityLevel, bytes)) return false;
    const auto label = reinterpret_cast<const TOKEN_MANDATORY_LABEL*>(bytes.data())->Label.Sid;
    if (!IsValidSid(label) || !*GetSidSubAuthorityCount(label)) return false;
    result.integrity = *GetSidSubAuthority(label, *GetSidSubAuthorityCount(label) - 1);
    DWORD used = 0, container = 0; TOKEN_ELEVATION elevation{}; TOKEN_STATISTICS statistics{};
    if (!GetTokenInformation(raw, TokenSessionId, &result.session, sizeof(result.session), &used)
        || !GetTokenInformation(raw, TokenElevation, &elevation, sizeof(elevation), &used)
        || !GetTokenInformation(raw, TokenStatistics, &statistics, sizeof(statistics), &used)
        || !GetTokenInformation(raw, TokenIsAppContainer, &container, sizeof(container), &used)) return false;
    result.elevated = elevation.TokenIsElevated != 0; result.authentication = statistics.AuthenticationId;
    return !container && result.session != 0 && result.integrity >= SECURITY_MANDATORY_MEDIUM_RID
        && result.integrity < SECURITY_MANDATORY_SYSTEM_RID;
}

bool sameLogon(const TokenIdentity& a, const TokenIdentity& b) noexcept
{
    return a.session == b.session && a.authentication.LowPart == b.authentication.LowPart
        && a.authentication.HighPart == b.authentication.HighPart && equalSid(a.user, b.user) && equalSid(a.logon, b.logon);
}

DWORD parentPid(HANDLE process) noexcept
{
    PROCESS_BASIC_INFORMATION info{}; ULONG used = 0;
    if (!NT_SUCCESS(NtQueryInformationProcess(process, ProcessBasicInformation, &info, sizeof(info), &used))) return 0;
    const auto pid = reinterpret_cast<std::uintptr_t>(info.InheritedFromUniqueProcessId);
    return pid <= MAXDWORD ? static_cast<DWORD>(pid) : 0;
}

bool verifyPeer(DWORD pid, const wchar_t* name, Peer& peer)
{
    if (!trustConfigured()) { SetLastError(trustNotConfigured); return false; }
    peer.process.reset(OpenProcess(PROCESS_QUERY_INFORMATION | SYNCHRONIZE, FALSE, pid));
    if (!peer.process) return false;
    const auto key = queryProcessIdentity(peer.process.get(), GenerationKind::creationTime);
    if (key.state != SampleState::valid || !readToken(peer.process.get(), peer.token)) return false;
    peer.key = key.identity;
    std::wstring path(32768, L'\0'); DWORD count = static_cast<DWORD>(path.size());
    if (!QueryFullProcessImageNameW(peer.process.get(), 0, path.data(), &count) || !count) return false;
    path.resize(count);
    const auto slash = path.find_last_of(L"\\/");
    if (slash == std::wstring::npos || _wcsicmp(path.c_str() + slash + 1, name) != 0) { SetLastError(trustRejected); return false; }
    peer.image.reset(CreateFileW(path.c_str(), GENERIC_READ | FILE_EXECUTE | SYNCHRONIZE, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr));
    if (!peer.image || peer.image.get() == INVALID_HANDLE_VALUE) return false;
    HANDLE image = peer.image.get();
    // Class 44 compares the kernel process image file object, not its mutable pathname or PEB.
    if (NtQueryInformationProcess(peer.process.get(), ProcessImageFileMapping, &image, sizeof(image), nullptr) != STATUS_SUCCESS) {
        SetLastError(trustRejected); return false;
    }
    if (!GetFileInformationByHandleEx(image, FileIdInfo, &peer.fileId, sizeof(peer.fileId)) || !lockPath(peer) || !signerPinned(image, peer.path)) {
        SetLastError(trustRejected); return false;
    }
    FILE_ID_INFO after{};
    if (!GetFileInformationByHandleEx(image, FileIdInfo, &after, sizeof(after)) || std::memcmp(&after, &peer.fileId, sizeof(after))
        || !peerAlive(peer)) { SetLastError(trustRejected); return false; }
    return true;
}

bool peerAlive(const Peer& peer) noexcept
{
    if (!peer.process || WaitForSingleObject(peer.process.get(), 0) != WAIT_TIMEOUT) return false;
    const auto actual = queryProcessIdentity(peer.process.get(), peer.key.generationKind);
    return actual.state == SampleState::valid && actual.identity == peer.key;
}
bool sameFile(const Peer& a, const Peer& b) noexcept
{
    return a.fileId.VolumeSerialNumber == b.fileId.VolumeSerialNumber
        && !std::memcmp(a.fileId.FileId.Identifier, b.fileId.FileId.Identifier, sizeof(a.fileId.FileId.Identifier));
}
}
