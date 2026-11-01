#include <phnt_windows.h>
#include <phnt.h>
#include "process_dump.hpp"
#include <algorithm>
#include <limits>

namespace nks::dump {
DWORD dumpError() noexcept { const auto value = GetLastError(); return value ? value : ERROR_GEN_FAILURE; }
namespace {
bool refuse(Result& result, State state, DWORD error) { result.state = state; result.error = error; return false; }
bool token(HANDLE process, std::vector<std::byte>& sid, DWORD& session)
{
    HANDLE raw = nullptr;
    if (!OpenProcessToken(process, TOKEN_QUERY, &raw)) return false;
    Handle held(raw);
    alignas(TOKEN_USER) std::array<std::byte, sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE> bytes{};
    DWORD used = 0;
    if (!GetTokenInformation(held.get(), TokenUser, bytes.data(), static_cast<DWORD>(bytes.size()), &used)) return false;
    const auto user = reinterpret_cast<const TOKEN_USER*>(bytes.data());
    if (!IsValidSid(user->User.Sid)) { SetLastError(ERROR_INVALID_SID); return false; }
    const auto length = GetLengthSid(user->User.Sid);
    if (!length || length > SECURITY_MAX_SID_SIZE) { SetLastError(ERROR_INVALID_SID); return false; }
    sid.resize(length);
    if (!CopySid(length, sid.data(), user->User.Sid)) return false;
    return GetTokenInformation(held.get(), TokenSessionId, &session, sizeof(session), &used) != FALSE;
}
bool serviceUser(const std::vector<std::byte>& sid)
{
    if (sid.empty()) return true;
    auto* value = const_cast<std::byte*>(sid.data());
    return IsWellKnownSid(value, WinLocalSystemSid) || IsWellKnownSid(value, WinLocalServiceSid) || IsWellKnownSid(value, WinNetworkServiceSid);
}
Architecture architecture(HANDLE process, DWORD& error)
{
    USHORT processMachine = 0, nativeMachine = 0;
    using Query = BOOL (WINAPI*)(HANDLE, USHORT*, USHORT*);
    const auto query = reinterpret_cast<Query>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "IsWow64Process2"));
    if (query) {
        if (!query(process, &processMachine, &nativeMachine)) { error = dumpError(); return Architecture::unknown; }
        if (nativeMachine == IMAGE_FILE_MACHINE_AMD64 && processMachine == IMAGE_FILE_MACHINE_I386) return Architecture::x86;
        if (nativeMachine == IMAGE_FILE_MACHINE_AMD64 && processMachine == IMAGE_FILE_MACHINE_UNKNOWN) return Architecture::x64;
    } else {
        SYSTEM_INFO info{}; GetNativeSystemInfo(&info); BOOL wow64 = FALSE;
        if (!IsWow64Process(process, &wow64)) { error = dumpError(); return Architecture::unknown; }
        if (info.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_AMD64) return wow64 ? Architecture::x86 : Architecture::x64;
    }
    error = ERROR_NOT_SUPPORTED; return Architecture::unknown;
}
bool userMatches(HANDLE process, const Context& context)
{
    std::vector<std::byte> sid; DWORD session = 0;
    if (!token(process, sid, session)) return false;
    if (!session || serviceUser(sid) || session != context.session || !EqualSid(sid.data(), const_cast<std::byte*>(context.user.data()))) {
        SetLastError(ERROR_ACCESS_DENIED); return false;
    }
    return true;
}
bool fileMatches(const Request& request, HANDLE file, Result& result)
{
    if (GetFileType(file) != FILE_TYPE_DISK) return refuse(result, State::invalidRequest, ERROR_INVALID_HANDLE);
    BY_HANDLE_FILE_INFORMATION info{}; LARGE_INTEGER size{};
    if (!GetFileInformationByHandle(file, &info) || !GetFileSizeEx(file, &size)
        || !GetFileInformationByHandleEx(file, FileIdInfo, &result.fileId, sizeof(result.fileId))) return refuse(result, State::error, dumpError());
    if ((info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) || info.nNumberOfLinks != 1 || size.QuadPart != 0)
        return refuse(result, State::invalidRequest, ERROR_INVALID_DATA);
    const DWORD chars = GetFinalPathNameByHandleW(file, nullptr, 0, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (!chars || chars > maximumPathChars) return refuse(result, State::error, chars ? ERROR_BUFFER_OVERFLOW : dumpError());
    std::wstring path(chars, L'\0');
    const DWORD written = GetFinalPathNameByHandleW(file, path.data(), chars, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (!written || written >= chars) return refuse(result, State::error, dumpError());
    path.resize(written);
    if (CompareStringOrdinal(path.data(), static_cast<int>(path.size()), request.temporary.data(), static_cast<int>(request.temporary.size()), TRUE) != CSTR_EQUAL)
        return refuse(result, State::invalidRequest, ERROR_INVALID_NAME);
    return true;
}
}

bool verifyDumpProcess(const Request& request, HANDLE process, Result& result)
{
    FILETIME created{}, ended{}, kernel{}, user{};
    if (!GetProcessTimes(process, &created, &ended, &kernel, &user)) { result.state = State::unavailable; result.error = dumpError(); return false; }
    result.observedCreated100ns = (static_cast<std::uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
    const auto actual = queryProcessIdentity(process, request.key.generationKind);
    if (actual.state != SampleState::valid) {
        result.state = actual.state == SampleState::accessDenied ? State::denied : State::unavailable;
        result.error = actual.state == SampleState::accessDenied ? ERROR_ACCESS_DENIED : ERROR_INVALID_DATA; return false;
    }
    if (actual.identity != request.key || result.observedCreated100ns != request.created100ns) {
        result.state = State::targetChanged; result.error = ERROR_RETRY; return false;
    }
    const auto wait = WaitForSingleObject(process, 0);
    if (wait != WAIT_TIMEOUT) {
        result.state = State::targetChanged; result.error = wait == WAIT_OBJECT_0 ? ERROR_PROCESS_ABORTED : dumpError(); return false;
    }
    return true;
}

bool dumpParentAlive(const Context& context)
{
    if (!context.parent || WaitForSingleObject(context.parent.get(), 0) != WAIT_TIMEOUT) return false;
    const auto current = queryProcessIdentity(context.parent.get(), context.parentKey.generationKind);
    return current.state == SampleState::valid && current.identity == context.parentKey;
}

bool prepareDump(const Request& request, Context& context, Result& result)
{
    if (!token(GetCurrentProcess(), context.user, context.session) || !context.session || serviceUser(context.user))
        return refuse(result, State::denied, ERROR_ACCESS_DENIED);
    PROCESS_BASIC_INFORMATION basic{}; ULONG used = 0;
    const auto status = NtQueryInformationProcess(GetCurrentProcess(), ProcessBasicInformation, &basic, sizeof(basic), &used);
    const auto parent = reinterpret_cast<std::uintptr_t>(basic.InheritedFromUniqueProcessId);
    if (!NT_SUCCESS(status) || used != sizeof(basic) || !parent || parent > std::numeric_limits<DWORD>::max() || parent != request.parentPid)
        return refuse(result, State::denied, ERROR_ACCESS_DENIED);
    if (!request.key.pid || request.key.pid == 4 || request.key.pid == GetCurrentProcessId())
        return refuse(result, State::denied, ERROR_ACCESS_DENIED);
    context.parent.reset(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_DUP_HANDLE | SYNCHRONIZE, FALSE, static_cast<DWORD>(parent)));
    if (!context.parent || !userMatches(context.parent.get(), context)) return refuse(result, State::denied, dumpError());
    const auto parentKey = queryProcessIdentity(context.parent.get(), GenerationKind::creationTime);
    const auto ownKey = queryProcessIdentity(GetCurrentProcess(), GenerationKind::creationTime);
    if (parentKey.state != SampleState::valid || ownKey.state != SampleState::valid || parentKey.identity.boot != ownKey.identity.boot
        || parentKey.identity.generation > ownKey.identity.generation) return refuse(result, State::denied, ERROR_INVALID_DATA);
    context.parentKey = parentKey.identity;
    if (!dumpParentAlive(context)) return refuse(result, State::denied, ERROR_PROCESS_ABORTED);
    context.process.reset(OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | SYNCHRONIZE, FALSE, request.key.pid));
    if (!context.process) return refuse(result, GetLastError() == ERROR_ACCESS_DENIED ? State::denied : State::unavailable, dumpError());
    if (!verifyDumpProcess(request, context.process.get(), result)) return false;
    BOOL critical = FALSE; PROCESS_PROTECTION_LEVEL_INFORMATION protection{};
    if (!IsProcessCritical(context.process.get(), &critical) || !GetProcessInformation(context.process.get(), ProcessProtectionLevelInfo, &protection, sizeof(protection)))
        return refuse(result, State::denied, dumpError());
    if (critical || protection.ProtectionLevel != PROTECTION_LEVEL_NONE || !userMatches(context.process.get(), context))
        return refuse(result, State::denied, ERROR_ACCESS_DENIED);
    result.architecture = architecture(context.process.get(), result.error);
    const auto ownArchitecture = sizeof(void*) == 8 ? Architecture::x64 : Architecture::x86;
    if (result.architecture != ownArchitecture) return refuse(result, State::unsupported, ERROR_NOT_SUPPORTED);
    if (request.outputHandle > std::numeric_limits<std::uintptr_t>::max()) return refuse(result, State::invalidRequest, ERROR_INVALID_HANDLE);
    HANDLE raw = nullptr;
    if (!DuplicateHandle(context.parent.get(), reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(request.outputHandle)), GetCurrentProcess(), &raw,
        FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FALSE, 0)) return refuse(result, State::error, dumpError());
    context.file.reset(raw);
    return fileMatches(request, context.file.get(), result);
}
}
