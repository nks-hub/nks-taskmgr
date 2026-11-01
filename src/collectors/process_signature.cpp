#include <phnt_windows.h>
#include <phnt.h>
#include "process_details.hpp"
#include <wincrypt.h>
#include <wintrust.h>
#include <softpub.h>
#include <mscat.h>
#include <bcrypt.h>
#include <algorithm>
#include <array>
#include <cstring>

namespace nks {
namespace {

constexpr std::uint64_t maximumSignatureFileBytes = 1024ull * 1024 * 1024;
constexpr unsigned maximumCatalogs = 32;

DetailHandle openLocalFile(const std::wstring& path, ProcessDetailsResult& result)
{
    std::wstring_view plain = path;
    if (plain.starts_with(L"\\\\?\\")) plain.remove_prefix(4);
    if (plain.size() < 3 || plain[1] != L':' || plain[2] != L'\\'
        || !((plain[0] >= L'A' && plain[0] <= L'Z') || (plain[0] >= L'a' && plain[0] <= L'z'))) {
        result.state = details::ResultState::unsupported; result.win32Error = ERROR_NOT_SUPPORTED; return {};
    }
    const wchar_t drive[]{plain[0], L':', L'\\', L'\0'}, deviceName[]{plain[0], L':', L'\0'};
    if (GetDriveTypeW(drive) != DRIVE_FIXED) { result.state = details::ResultState::unsupported; result.win32Error = ERROR_NOT_SUPPORTED; return {}; }
    std::array<wchar_t, 32768> device{};
    if (!QueryDosDeviceW(deviceName, device.data(), static_cast<DWORD>(device.size()))) {
        result.state = details::ResultState::unavailable; result.win32Error = GetLastError(); return {};
    }
    const std::wstring_view target(device.data());
    if (!target.starts_with(L"\\Device\\HarddiskVolume")) {
        result.state = details::ResultState::unsupported; result.win32Error = ERROR_NOT_SUPPORTED; return {};
    }
    const std::wstring native = std::wstring(target) + std::wstring(plain.substr(2));
    if (native.size() > details::maximumStringChars) { result.state = details::ResultState::limitExceeded; return {}; }
    UNICODE_STRING name{};
    name.Buffer = const_cast<PWSTR>(native.c_str());
    name.Length = name.MaximumLength = static_cast<USHORT>(native.size() * sizeof(wchar_t));
    OBJECT_ATTRIBUTES attributes{};
    InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE, nullptr, nullptr);
    HANDLE file = nullptr; IO_STATUS_BLOCK io{};
    // A local device path and OBJ_DONT_REPARSE prevent UNC, junction and cloud recall traversal.
    const auto status = NtCreateFile(&file, GENERIC_READ | SYNCHRONIZE, &attributes, &io, nullptr, FILE_ATTRIBUTE_NORMAL,
        FILE_SHARE_READ, FILE_OPEN, FILE_NON_DIRECTORY_FILE | FILE_SYNCHRONOUS_IO_NONALERT | FILE_OPEN_NO_RECALL, nullptr, 0);
    if (!NT_SUCCESS(status)) {
        result.ntStatus = status; result.win32Error = RtlNtStatusToDosError(status);
        result.state = status == STATUS_ACCESS_DENIED ? details::ResultState::accessDenied : details::ResultState::unavailable;
        return {};
    }
    DetailHandle owned(file);
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size)) { result.state = details::ResultState::unavailable; result.win32Error = GetLastError(); return {}; }
    if (size.QuadPart < 0 || static_cast<std::uint64_t>(size.QuadPart) > maximumSignatureFileBytes) {
        result.state = details::ResultState::limitExceeded; return {};
    }
    return owned;
}

struct TrustState {
    GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    WINTRUST_DATA data{};
    TrustState() {
        data.cbStruct = sizeof(data); data.dwUIChoice = WTD_UI_NONE; data.fdwRevocationChecks = WTD_REVOKE_NONE;
        data.dwStateAction = WTD_STATEACTION_VERIFY;
        data.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE | WTD_DISABLE_MD2_MD4;
    }
    ~TrustState() {
        if (data.hWVTStateData) {
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(reinterpret_cast<HWND>(INVALID_HANDLE_VALUE), &action, &data);
        }
    }
    LONG verify() { return WinVerifyTrust(reinterpret_cast<HWND>(INVALID_HANDLE_VALUE), &action, &data); }
};

std::wstring publisher(HANDLE state)
{
    if (!state) return {};
    auto* provider = WTHelperProvDataFromStateData(state);
    if (!provider) return {};
    auto* signer = WTHelperGetProvSignerFromChain(provider, 0, FALSE, 0);
    if (!signer || !signer->csCertChain || !signer->pasCertChain || !signer->pasCertChain[0].pCert) return {};
    const auto certificate = signer->pasCertChain[0].pCert;
    const auto size = CertGetNameStringW(certificate, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, nullptr, nullptr, 0);
    if (!size || size > details::maximumStringChars + 1) return {};
    std::wstring text(size, L'\0');
    const auto written = CertGetNameStringW(certificate, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, nullptr, text.data(), size);
    if (!written || written > size) return {};
    text.resize(written - 1); return text;
}

bool unavailableTrust(LONG status)
{
    return status == TRUST_E_PROVIDER_UNKNOWN || status == TRUST_E_SUBJECT_FORM_UNKNOWN || status == TRUST_E_SYSTEM_ERROR
        || status == CRYPT_E_FILE_ERROR || status == CRYPT_E_REVOCATION_OFFLINE || status == CERT_E_REVOCATION_FAILURE
        || status == E_OUTOFMEMORY || status == HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED)
        || status == HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION);
}

ProcessFileSignature verdict(const std::wstring& path, TrustState& trust, LONG status, details::SignatureSource source)
{
    ProcessFileSignature result;
    result.path = path; result.source = source; result.trustCode = status;
    result.state = status == ERROR_SUCCESS ? details::SignatureState::valid : status == TRUST_E_NOSIGNATURE
        ? details::SignatureState::unsignedFile : unavailableTrust(status) ? details::SignatureState::unavailable : details::SignatureState::untrusted;
    result.publisher = publisher(trust.data.hWVTStateData);
    return result;
}

struct CatalogAdmin {
    HCATADMIN value{};
    ~CatalogAdmin() { if (value) CryptCATAdminReleaseContext(value, 0); }
};
struct CatalogContext {
    HCATADMIN admin{};
    HCATINFO value{};
    ~CatalogContext() { if (value) CryptCATAdminReleaseCatalogContext(admin, value, 0); }
};

bool normalCatalogEnd(DWORD error)
{
    return error == ERROR_SUCCESS || error == ERROR_NOT_FOUND || error == ERROR_NO_MORE_ITEMS || error == ERROR_NO_MORE_FILES;
}

void catalogSignatures(HANDLE file, const std::wstring& path, ProcessDetailsResult& result)
{
    const auto library = GetModuleHandleW(L"wintrust.dll");
    const auto acquire = reinterpret_cast<decltype(&CryptCATAdminAcquireContext2)>(GetProcAddress(library, "CryptCATAdminAcquireContext2"));
    const auto hashFile = reinterpret_cast<decltype(&CryptCATAdminCalcHashFromFileHandle2)>(GetProcAddress(library, "CryptCATAdminCalcHashFromFileHandle2"));
    if (!acquire || !hashFile) { result.state = details::ResultState::unsupported; result.win32Error = ERROR_CALL_NOT_IMPLEMENTED; return; }
    DWORD failure = 0;
    unsigned visited = 0;
    for (const auto algorithm : {BCRYPT_SHA256_ALGORITHM, BCRYPT_SHA1_ALGORITHM}) {
        CatalogAdmin admin;
        if (!acquire(&admin.value, nullptr, algorithm, nullptr, 0)) { failure = GetLastError(); continue; }
        LARGE_INTEGER zero{};
        if (!SetFilePointerEx(file, zero, nullptr, FILE_BEGIN)) { failure = GetLastError(); continue; }
        DWORD hashSize = 0;
        if (!hashFile(admin.value, file, &hashSize, nullptr, 0)) { failure = GetLastError(); continue; }
        std::array<BYTE, 128> hash{};
        if (!hashSize || hashSize > hash.size()) { failure = ERROR_INVALID_DATA; continue; }
        if (!SetFilePointerEx(file, zero, nullptr, FILE_BEGIN) || !hashFile(admin.value, file, &hashSize, hash.data(), 0)) {
            failure = GetLastError(); continue;
        }
        if (!hashSize || hashSize > hash.size()) { failure = ERROR_INVALID_DATA; continue; }
        std::array<wchar_t, 257> tag{};
        constexpr wchar_t hex[] = L"0123456789ABCDEF";
        for (DWORD i = 0; i < hashSize; ++i) { tag[i * 2] = hex[hash[i] >> 4]; tag[i * 2 + 1] = hex[hash[i] & 15]; }
        SetLastError(ERROR_SUCCESS);
        CatalogContext catalog{admin.value, CryptCATAdminEnumCatalogFromHash(admin.value, hash.data(), hashSize, 0, nullptr)};
        if (!catalog.value && !normalCatalogEnd(GetLastError())) { failure = GetLastError(); continue; }
        while (catalog.value) {
            if (++visited > maximumCatalogs) { result.state = details::ResultState::limitExceeded; return; }
            CATALOG_INFO info{}; info.cbStruct = sizeof(info);
            if (!CryptCATCatalogInfoFromContext(catalog.value, &info, 0)) { failure = GetLastError(); break; }
            if (std::find(std::begin(info.wszCatalogFile), std::end(info.wszCatalogFile), L'\0') == std::end(info.wszCatalogFile)) {
                failure = ERROR_INVALID_DATA; break;
            }
            WINTRUST_CATALOG_INFO member{}; member.cbStruct = sizeof(member);
            member.pcwszCatalogFilePath = info.wszCatalogFile; member.pcwszMemberTag = tag.data();
            member.pcwszMemberFilePath = path.c_str(); member.hMemberFile = file;
            member.pbCalculatedFileHash = hash.data(); member.cbCalculatedFileHash = hashSize; member.hCatAdmin = admin.value;
            TrustState trust; trust.data.dwUnionChoice = WTD_CHOICE_CATALOG; trust.data.pCatalog = &member;
            auto candidate = verdict(path, trust, trust.verify(), details::SignatureSource::catalog);
            if (candidate.state == details::SignatureState::valid) { result.signature = std::move(candidate); result.state = details::ResultState::ok; return; }
            result.signature = std::move(candidate);
            // Enumeration releases the previous context; only an early exit retains one to close.
            SetLastError(ERROR_SUCCESS);
            catalog.value = CryptCATAdminEnumCatalogFromHash(admin.value, hash.data(), hashSize, 0, &catalog.value);
            if (!catalog.value && !normalCatalogEnd(GetLastError())) failure = GetLastError();
        }
    }
    if (failure) { result.state = details::ResultState::unavailable; result.win32Error = failure; }
}

}

void readFileSignatureOnDisk(const std::wstring& path, ProcessDetailsResult& result)
{
    auto file = openLocalFile(path, result);
    if (!file) return;
    WINTRUST_FILE_INFO info{}; info.cbStruct = sizeof(info); info.pcwszFilePath = path.c_str(); info.hFile = file.get();
    TrustState trust; trust.data.dwUnionChoice = WTD_CHOICE_FILE; trust.data.pFile = &info;
    result.signature = verdict(path, trust, trust.verify(), details::SignatureSource::embedded);
    result.state = details::ResultState::ok;
    if (result.signature->state == details::SignatureState::unsignedFile) {
        result.signature->source = details::SignatureSource::none;
        catalogSignatures(file.get(), path, result);
    }
}

}
