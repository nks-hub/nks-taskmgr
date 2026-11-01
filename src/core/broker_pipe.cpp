#include "broker_pipe.hpp"
#include <aclapi.h>
#include <bcrypt.h>
#include <algorithm>

namespace nks::broker {
namespace {
bool complete(HANDLE pipe, OVERLAPPED& operation, HANDLE peer, ULONGLONG deadline, DWORD& count)
{
    const ULONGLONG now = GetTickCount64();
    HANDLE waits[]{operation.hEvent, peer};
    DWORD wait = now < deadline ? WaitForMultipleObjects(peer ? 2 : 1, waits, FALSE,
        static_cast<DWORD>(std::min<ULONGLONG>(deadline - now, MAXDWORD - 1))) : WAIT_TIMEOUT;
    if (wait == WAIT_OBJECT_0 && GetOverlappedResult(pipe, &operation, &count, FALSE)) return true;
    DWORD error = wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : ERROR_OPERATION_ABORTED;
    CancelIoEx(pipe, &operation);
    // OVERLAPPED storage stays alive until the cancelled kernel operation completes.
    GetOverlappedResult(pipe, &operation, &count, TRUE); SetLastError(error); return false;
}
}
Handle createPipe(const std::wstring& name, const TokenIdentity& owner)
{
    EXPLICIT_ACCESSW access{}; access.grfAccessPermissions = FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES
        | FILE_WRITE_ATTRIBUTES | READ_CONTROL | SYNCHRONIZE;
    access.grfAccessMode = SET_ACCESS; access.grfInheritance = NO_INHERITANCE;
    access.Trustee.TrusteeForm = TRUSTEE_IS_SID; access.Trustee.TrusteeType = TRUSTEE_IS_GROUP;
    access.Trustee.ptstrName = reinterpret_cast<LPWSTR>(const_cast<std::byte*>(owner.logon.data()));
    PACL acl = nullptr; DWORD error = SetEntriesInAclW(1, &access, nullptr, &acl);
    if (error) { SetLastError(error); return {}; }
    SECURITY_DESCRIPTOR descriptor{}; InitializeSecurityDescriptor(&descriptor, SECURITY_DESCRIPTOR_REVISION);
    bool valid = SetSecurityDescriptorDacl(&descriptor, TRUE, acl, FALSE) != FALSE;
    std::array<std::byte, SECURITY_MAX_SID_SIZE> medium{}; DWORD sidSize = static_cast<DWORD>(medium.size());
    alignas(ACL) std::array<std::byte, 128> label{}; auto* sacl = reinterpret_cast<ACL*>(label.data());
    valid = valid && CreateWellKnownSid(WinMediumLabelSid, nullptr, medium.data(), &sidSize)
        && InitializeAcl(sacl, static_cast<DWORD>(label.size()), ACL_REVISION)
        && AddMandatoryAce(sacl, ACL_REVISION, 0, SYSTEM_MANDATORY_LABEL_NO_WRITE_UP, medium.data())
        && SetSecurityDescriptorSacl(&descriptor, TRUE, sacl, FALSE);
    SECURITY_ATTRIBUTES attributes{sizeof(attributes), &descriptor, FALSE};
    HANDLE pipe = valid ? CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 65536, 65536, 0, &attributes) : INVALID_HANDLE_VALUE;
    error = GetLastError(); LocalFree(acl); SetLastError(error); return Handle(pipe);
}
bool connectPipe(HANDLE pipe, HANDLE peer, ULONGLONG deadline)
{
    Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr)); if (!event) return false;
    OVERLAPPED operation{}; operation.hEvent = event.get();
    if (ConnectNamedPipe(pipe, &operation)) return true;
    const DWORD error = GetLastError(); if (error == ERROR_PIPE_CONNECTED) return true;
    if (error != ERROR_IO_PENDING) return false;
    DWORD count = 0; return complete(pipe, operation, peer, deadline, count);
}
bool pipeIo(HANDLE pipe, std::span<std::byte> bytes, bool writing, HANDLE peer, ULONGLONG deadline)
{
    Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr)); if (!event) return false;
    while (!bytes.empty()) {
        if (GetTickCount64() >= deadline || (peer && WaitForSingleObject(peer, 0) != WAIT_TIMEOUT)) {
            SetLastError(ERROR_TIMEOUT); return false;
        }
        ResetEvent(event.get()); OVERLAPPED operation{}; operation.hEvent = event.get(); DWORD count = 0;
        const DWORD size = static_cast<DWORD>(std::min<std::size_t>(bytes.size(), 65536));
        const BOOL done = writing ? WriteFile(pipe, bytes.data(), size, &count, &operation) : ReadFile(pipe, bytes.data(), size, &count, &operation);
        if (!done && (GetLastError() != ERROR_IO_PENDING || !complete(pipe, operation, peer, deadline, count))) return false;
        if (!count || count > size) { SetLastError(ERROR_BROKEN_PIPE); return false; }
        bytes = bytes.subspan(count);
    }
    return true;
}
bool pipeWrite(HANDLE pipe, std::span<const std::byte> bytes, HANDLE peer, ULONGLONG deadline)
{
    return pipeIo(pipe, {const_cast<std::byte*>(bytes.data()), bytes.size()}, true, peer, deadline);
}
std::wstring newPipeName()
{
    std::array<BYTE, 16> random{};
    if (BCryptGenRandom(nullptr, random.data(), static_cast<ULONG>(random.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) return {};
    constexpr wchar_t digits[] = L"0123456789abcdef"; std::wstring name = L"\\\\.\\pipe\\NksTaskMgr-";
    for (auto value : random) { name += digits[value >> 4]; name += digits[value & 15]; }
    return name;
}
}
