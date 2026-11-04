#include <phnt_windows.h>
#include <phnt.h>
#include "process_actions.hpp"
#include "process_details.hpp"
#include <array>
#include <limits>
#include <algorithm>

namespace nks::actions {
namespace {
DWORD lastError() { const auto value = GetLastError(); return value ? value : ERROR_GEN_FAILURE; }

bool tokenIdentity(HANDLE process, std::vector<std::byte>& sid, DWORD& session)
{
    HANDLE raw = nullptr;
    if (!OpenProcessToken(process, TOKEN_QUERY, &raw)) return false;
    Handle token(raw);
    alignas(TOKEN_USER) std::array<std::byte, sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE> bytes{};
    DWORD used = 0;
    if (!GetTokenInformation(token.get(), TokenUser, bytes.data(), static_cast<DWORD>(bytes.size()), &used)) return false;
    const auto user = reinterpret_cast<const TOKEN_USER*>(bytes.data());
    if (!IsValidSid(user->User.Sid)) { SetLastError(ERROR_INVALID_SID); return false; }
    const DWORD length = GetLengthSid(user->User.Sid);
    if (!length || length > SECURITY_MAX_SID_SIZE) { SetLastError(ERROR_INVALID_SID); return false; }
    sid.resize(length);
    if (!CopySid(length, sid.data(), user->User.Sid)) return false;
    return GetTokenInformation(token.get(), TokenSessionId, &session, sizeof(session), &used) != FALSE;
}

bool systemUser(const std::vector<std::byte>& sid)
{
    auto* value = const_cast<std::byte*>(sid.data());
    return sid.empty() || IsWellKnownSid(value, WinLocalSystemSid) || IsWellKnownSid(value, WinLocalServiceSid) || IsWellKnownSid(value, WinNetworkServiceSid);
}

bool created(HANDLE process, std::uint64_t& value)
{
    FILETIME start{}, end{}, kernel{}, user{};
    if (!GetProcessTimes(process, &start, &end, &kernel, &user)) return false;
    value = (static_cast<std::uint64_t>(start.dwHighDateTime) << 32) | start.dwLowDateTime;
    if (!value) { SetLastError(ERROR_INVALID_DATA); return false; }
    return true;
}

void refuse(Result& result, Reason reason, DWORD error = ERROR_ACCESS_DENIED)
{
    result.outcome = Outcome::refused; result.reason = reason; result.error = error;
}

bool protectedTarget(HANDLE process, Result& result)
{
    BOOL critical = FALSE;
    if (!IsProcessCritical(process, &critical)) { refuse(result, Reason::protectionUnverifiable, lastError()); return true; }
    if (critical) { refuse(result, Reason::critical); return true; }
    PROCESS_PROTECTION_LEVEL_INFORMATION protection{};
    if (!GetProcessInformation(process, ProcessProtectionLevelInfo, &protection, sizeof(protection))) {
        refuse(result, Reason::protectionUnverifiable, lastError()); return true;
    }
    if (protection.ProtectionLevel != PROTECTION_LEVEL_NONE) { refuse(result, Reason::protectedTarget); return true; }
    return false;
}

bool verify(HANDLE process, Result& result, bool before)
{
    if (!created(process, result.observedCreated100ns)) { refuse(result, Reason::identityUnverifiable, lastError()); return false; }
    const auto actual = queryProcessIdentity(process, result.target.key.generationKind);
    if (actual.state != SampleState::valid) {
        refuse(result, Reason::identityUnverifiable, actual.state == SampleState::accessDenied ? ERROR_ACCESS_DENIED
            : actual.state == SampleState::unsupported ? ERROR_NOT_SUPPORTED : ERROR_INVALID_DATA); return false;
    }
    if (actual.identity != result.target.key) {
        result.outcome = Outcome::identityChanged; result.reason = Reason::identityUnverifiable; result.error = ERROR_RETRY; return false;
    }
    if (result.observedCreated100ns != result.target.created100ns) {
        result.outcome = Outcome::identityChanged; result.reason = Reason::rawTimeMismatch; result.error = ERROR_RETRY; return false;
    }
    result.flags |= identityVerified;
    const auto wait = WaitForSingleObject(process, 0);
    if (wait == WAIT_OBJECT_0) {
        result.flags |= exitedAfter; result.flags &= ~static_cast<std::uint32_t>(aliveAfter);
        result.outcome = Outcome::alreadyExited; result.error = ERROR_PROCESS_ABORTED; return false;
    }
    if (wait != WAIT_TIMEOUT) { refuse(result, Reason::identityUnverifiable, lastError()); return false; }
    if (before) result.flags |= aliveBefore;
    return true;
}

void metadata(HANDLE process, Result& result)
{
    result.flags &= ~static_cast<std::uint32_t>(priorityAvailable | affinityAvailable);
    result.priority = result.group = 0; result.processMask = result.systemMask = 0;
    result.priorityError = result.affinityError = ERROR_NO_DATA;
    const auto priority = GetPriorityClass(process);
    if (priority) { result.priority = priority; result.priorityError = 0; result.flags |= priorityAvailable; }
    else result.priorityError = lastError();
    USHORT count = 1, group = 0;
    if (!GetProcessGroupAffinity(process, &count, &group)) {
        result.affinityError = count > 1 ? ERROR_NOT_SUPPORTED : lastError(); return;
    }
    if (count != 1) { result.affinityError = ERROR_NOT_SUPPORTED; return; }
    DWORD_PTR mask = 0, system = 0;
    if (!GetProcessAffinityMask(process, &mask, &system)) { result.affinityError = lastError(); return; }
    if (!mask || !system || (mask & ~system)) { result.affinityError = ERROR_NOT_SUPPORTED; return; }
    result.processMask = mask; result.systemMask = system; result.group = group; result.affinityError = 0; result.flags |= affinityAvailable;
}

bool parentAlive(const Context& context)
{
    if (!context.ready || !context.parent || WaitForSingleObject(context.parent.get(), 0) != WAIT_TIMEOUT) return false;
    if (context.brokerClient) {
        const auto client = queryProcessIdentity(context.brokerClient.get(), context.brokerClientKey.generationKind);
        if (WaitForSingleObject(context.brokerClient.get(), 0) != WAIT_TIMEOUT
            || client.state != SampleState::valid || client.identity != context.brokerClientKey) return false;
    }
    const auto parent = queryProcessIdentity(context.parent.get(), context.parentKey.generationKind);
    return parent.state == SampleState::valid && parent.identity == context.parentKey;
}

void lifeAfter(HANDLE process, Command command, Result& result)
{
    result.flags &= ~static_cast<std::uint32_t>(aliveAfter | exitedAfter);
    const auto wait = WaitForSingleObject(process, 0);
    if (wait == WAIT_TIMEOUT) result.flags |= aliveAfter;
    else if (wait == WAIT_OBJECT_0) {
        result.flags |= exitedAfter;
        if (command == Command::terminate && result.outcome == Outcome::terminationRequested) {
            result.outcome = Outcome::applied; result.flags |= effectConfirmed;
        } else if (command != Command::closeWindows && command != Command::closeHandle) {
            result.outcome = Outcome::alreadyExited; result.flags &= ~static_cast<std::uint32_t>(effectConfirmed);
        }
    } else {
        result.outcome = result.flags & mayHaveApplied ? Outcome::unverified : Outcome::error;
        result.reason = Reason::identityUnverifiable; result.error = lastError(); result.flags &= ~static_cast<std::uint32_t>(effectConfirmed);
    }
}

struct WindowClose {
    const Context& context;
    HANDLE process;
    Result& result;
    unsigned examined{};
    bool limited{}, aborted{};

    bool post(HWND window)
    {
        DWORD pid = 0;
        if (!IsWindow(window) || !IsWindowVisible(window) || GetAncestor(window, GA_ROOT) != window
            || !GetWindowThreadProcessId(window, &pid) || pid != result.target.key.pid) return false;
        if (!parentAlive(context)) { refuse(result, Reason::parentUnavailable); aborted = true; return false; }
        if (!verify(process, result, false) || protectedTarget(process, result)) { aborted = true; return false; }
        DWORD currentPid = 0;
        if (!GetWindowThreadProcessId(window, &currentPid) || currentPid != pid) return false;
        if (PostMessageW(window, WM_CLOSE, 0, 0)) { ++result.windowsPosted; result.flags |= mayHaveApplied; }
        else { ++result.windowsFailed; result.error = lastError(); }
        return true;
    }
};

BOOL CALLBACK closeWindow(HWND window, LPARAM parameter) noexcept
{
    auto& close = *reinterpret_cast<WindowClose*>(parameter);
    if (++close.examined > 32768 || close.result.windowsPosted + close.result.windowsFailed >= 256) { close.limited = true; return FALSE; }
    try { close.post(window); return close.aborted ? FALSE : TRUE; }
    catch (...) { close.aborted = true; close.result.error = ERROR_GEN_FAILURE; return FALSE; }
}

void closeWindows(const Context& context, HANDLE process, Result& result)
{
    WindowClose close{context, process, result};
    if (result.target.window) {
        if (!close.post(reinterpret_cast<HWND>(static_cast<std::uintptr_t>(result.target.window))) && !close.aborted)
            refuse(result, Reason::windowMismatch, ERROR_INVALID_WINDOW_HANDLE);
    } else {
        SetLastError(ERROR_SUCCESS);
        if (!EnumWindows(closeWindow, reinterpret_cast<LPARAM>(&close)) && !close.limited && !close.aborted) {
            result.error = lastError(); result.outcome = Outcome::error;
        }
    }
    if (result.windowsPosted) {
        result.outcome = Outcome::closeRequested;
        result.reason = close.limited ? Reason::windowLimit : close.aborted || result.windowsFailed ? Reason::partialClose : Reason::none;
    } else if (close.limited) refuse(result, Reason::windowLimit, ERROR_MORE_DATA);
    else if (!close.aborted && result.reason != Reason::windowMismatch && result.outcome != Outcome::error)
        refuse(result, result.windowsFailed ? Reason::accessDenied : Reason::noWindows, result.windowsFailed ? result.error : ERROR_NOT_FOUND);
}
}

bool priorityAllowed(std::uint64_t value) noexcept
{
    return value == IDLE_PRIORITY_CLASS || value == BELOW_NORMAL_PRIORITY_CLASS || value == NORMAL_PRIORITY_CLASS
        || value == ABOVE_NORMAL_PRIORITY_CLASS || value == HIGH_PRIORITY_CLASS;
}

Context actionContext()
{
    Context context; context.ownPid = GetCurrentProcessId();
    if (!tokenIdentity(GetCurrentProcess(), context.user, context.session)) return context;
    if (!context.session || systemUser(context.user)) { context.denial = Reason::systemTarget; return context; }
    PROCESS_BASIC_INFORMATION basic{}; ULONG used = 0;
    const auto status = NtQueryInformationProcess(GetCurrentProcess(), ProcessBasicInformation, &basic, sizeof(basic), &used);
    const auto parentPid = reinterpret_cast<std::uintptr_t>(basic.InheritedFromUniqueProcessId);
    if (!NT_SUCCESS(status) || used != sizeof(basic) || !parentPid || parentPid > std::numeric_limits<DWORD>::max()) return context;
    context.parentPid = static_cast<DWORD>(parentPid);
    if (context.parentPid == context.ownPid || context.parentPid == 4) return context;
    context.parent.reset(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, context.parentPid));
    if (!context.parent || WaitForSingleObject(context.parent.get(), 0) != WAIT_TIMEOUT) return context;
    const auto parent = queryProcessIdentity(context.parent.get(), GenerationKind::creationTime);
    const auto own = queryProcessIdentity(GetCurrentProcess(), GenerationKind::creationTime);
    if (parent.state != SampleState::valid || own.state != SampleState::valid || parent.identity.pid != context.parentPid
        || parent.identity.boot != own.identity.boot || parent.identity.generation > own.identity.generation) return context;
    std::vector<std::byte> user; DWORD session = 0;
    if (!tokenIdentity(context.parent.get(), user, session) || session != context.session || !EqualSid(user.data(), context.user.data())) return context;
    context.parentKey = parent.identity; context.ready = true; return context;
}

namespace {
bool targetAllowed(const Context& context, Result& result)
{
    const auto pid = result.target.key.pid;
    if (!pid || pid == 4) { refuse(result, Reason::systemTarget); return false; }
    if (pid == context.ownPid || pid == context.parentPid
        || std::find(context.protectedPids.begin(), context.protectedPids.end(), pid) != context.protectedPids.end()) { refuse(result, Reason::ownOrParent); return false; }
    if (!parentAlive(context)) { refuse(result, context.ready ? Reason::parentUnavailable : context.denial); return false; }
    return true;
}

// sameUser keeps the GUI user's SID mandatory even after broker consent, which excludes DWM/UMFD-style session accounts.
bool admitted(const Context& context, HANDLE process, Result& result, bool sameUser = false)
{
    if (!targetAllowed(context, result)) return false;
    if (!process) { refuse(result, Reason::identityUnverifiable, ERROR_INVALID_HANDLE); return false; }
    if (!verify(process, result, true) || protectedTarget(process, result)) return false;
    std::vector<std::byte> user; DWORD session = 0;
    if (!tokenIdentity(process, user, session)) { refuse(result, Reason::protectionUnverifiable, lastError()); return false; }
    if (!session || systemUser(user)) { refuse(result, Reason::systemTarget); return false; }
    if (session != context.session || ((sameUser || !context.brokerConsented())
        && !EqualSid(user.data(), const_cast<std::byte*>(context.user.data())))) { refuse(result, Reason::ownerMismatch); return false; }
    return true;
}

HANDLE handleValue(const HandleSpec& handle) { return reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(handle.value)); }

// Rereads the confirmed row from the target's own table and pins the object it names now.
bool matchHandle(HANDLE process, const HandleSpec& handle, Result& result, Handle& pinned)
{
    // A file object without a readable name has nothing that ties it to the confirmed file.
    if (handle.type == L"File" && !(handle.flags & verifyHandleName)) { refuse(result, Reason::handleUnverifiable, ERROR_INVALID_DATA); return false; }
    ProcessDetailsResult table;
    readProcessHandles(process, table);
    if (table.state != details::ResultState::ok) {
        const bool denied = table.state == details::ResultState::accessDenied;
        refuse(result, denied ? Reason::accessDenied : Reason::handleUnverifiable,
            table.win32Error ? table.win32Error : denied ? ERROR_ACCESS_DENIED : ERROR_INVALID_DATA);
        result.ntStatus = table.ntStatus; return false;
    }
    const auto entry = std::find_if(table.handles.begin(), table.handles.end(), [&](const auto& row) { return row.value == handle.value; });
    if (entry == table.handles.end()) { refuse(result, Reason::handleMissing, ERROR_INVALID_HANDLE); return false; }
    if (entry->grantedAccess != handle.grantedAccess || entry->typeIndex != handle.typeIndex) { refuse(result, Reason::handleChanged, ERROR_RETRY); return false; }
    if (entry->attributes & OBJ_PROTECT_CLOSE) { refuse(result, Reason::handleProtected); return false; }
    HANDLE raw = nullptr;
    if (!DuplicateHandle(process, handleValue(handle), GetCurrentProcess(), &raw, 0, FALSE, 0)) {
        const auto error = lastError();
        refuse(result, error == ERROR_ACCESS_DENIED ? Reason::accessDenied : error == ERROR_INVALID_HANDLE ? Reason::handleMissing : Reason::handleUnverifiable, error);
        return false;
    }
    pinned.reset(raw);
    std::wstring text;
    for (const bool name : {false, true}) {
        if (name && !(handle.flags & verifyHandleName)) break;
        const auto status = queryObjectText(raw, name, text);
        if (!NT_SUCCESS(status)) { refuse(result, Reason::handleUnverifiable, RtlNtStatusToDosError(status)); result.ntStatus = status; return false; }
        if (text != (name ? handle.name : handle.type)) { refuse(result, Reason::handleChanged, ERROR_RETRY); return false; }
    }
    if (handle.fingerprint.kind != static_cast<std::uint32_t>(details::FingerprintKind::none)) {
        details::HandleFingerprint actual;
        if (!readHandleFingerprint(process, handle.value, raw, handle.type, actual)) { refuse(result, Reason::handleUnverifiable, ERROR_INVALID_DATA); return false; }
        if (actual != handle.fingerprint) { refuse(result, Reason::handleChanged, ERROR_RETRY); return false; }
    }
    return true;
}
}

void closeVerifiedHandle(const HandleSpec& handle, HANDLE process, HANDLE pinned, Result& result)
{
    result.flags |= mayHaveApplied;
    HANDLE raw = nullptr;
    // DUPLICATE_CLOSE_SOURCE closes the value even if the copy fails; the copy shows which object left the slot.
    if (DuplicateHandle(process, handleValue(handle), GetCurrentProcess(), &raw, 0, FALSE, DUPLICATE_CLOSE_SOURCE)) {
        Handle closed(raw);
        if (NtCompareObjects(pinned, closed.get()) == STATUS_SUCCESS) {
            result.outcome = Outcome::applied; result.flags |= effectConfirmed;
            if (((handle.flags & verifyHandleName) && !handle.name.empty())
                || handle.fingerprint.kind != static_cast<std::uint32_t>(details::FingerprintKind::none)) result.flags |= objectMatched;
        } else { result.outcome = Outcome::unverified; result.reason = Reason::handleChanged; result.error = ERROR_RETRY; }
    } else if ((result.error = lastError()) == ERROR_INVALID_HANDLE) {
        refuse(result, Reason::handleMissing, ERROR_INVALID_HANDLE); result.flags &= ~static_cast<std::uint32_t>(mayHaveApplied); return;
    } else { result.outcome = Outcome::unverified; result.reason = Reason::handleUnverifiable; }
    // A close-protected or silently refused close leaves the same object under the value.
    HANDLE probe = nullptr;
    if (DuplicateHandle(process, handleValue(handle), GetCurrentProcess(), &probe, 0, FALSE, 0)) {
        Handle still(probe);
        if (NtCompareObjects(still.get(), pinned) == STATUS_SUCCESS) {
            result.outcome = Outcome::unverified; result.reason = Reason::handleProtected; result.error = ERROR_RETRY;
            result.flags &= ~static_cast<std::uint32_t>(effectConfirmed | objectMatched);
        }
    }
}

void closeHeldHandle(const Context& context, const HandleSpec& handle, HANDLE process, Result& result)
{
    if (!admitted(context, process, result, true)) return;
    Handle pinned;
    if (!matchHandle(process, handle, result, pinned)) return;
    if (!parentAlive(context)) { refuse(result, Reason::parentUnavailable); return; }
    if (!verify(process, result, false) || protectedTarget(process, result)) return;
    closeVerifiedHandle(handle, process, pinned.get(), result);
    lifeAfter(process, Command::closeHandle, result);
}

void closeHandle(const Context& context, const HandleSpec& handle, Result& result)
{
    if (!targetAllowed(context, result)) return;
    const auto pid = result.target.key.pid;
    Handle process(OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE | SYNCHRONIZE, FALSE, pid));
    if (!process) {
        const auto error = lastError();
        // A denied open of a PPL or critical target is reported as protection, not as a plain access failure.
        Handle limited(error == ERROR_ACCESS_DENIED ? OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid) : nullptr);
        if (!limited || !protectedTarget(limited.get(), result))
            refuse(result, error == ERROR_ACCESS_DENIED ? Reason::accessDenied : Reason::identityUnverifiable, error);
        return;
    }
    closeHeldHandle(context, handle, process.get(), result);
}

void applyHeldAction(const Context& context, Command command, std::uint64_t argument, std::uint32_t group, HANDLE process,
    Result& result)
{
    if (!admitted(context, process, result)) return;
    metadata(process, result);
    if (command == Command::query) { result.outcome = Outcome::inspected; lifeAfter(process, command, result); return; }
    if (command == Command::affinity && (!(result.flags & affinityAvailable) || result.group != group)) {
        refuse(result, Reason::multipleGroups, result.affinityError ? result.affinityError : ERROR_NOT_SUPPORTED); return;
    }
    if (command == Command::affinity && (!argument || (argument & ~result.systemMask))) { refuse(result, Reason::invalidArgument, ERROR_INVALID_PARAMETER); return; }
    if (command == Command::priority && !priorityAllowed(argument)) { refuse(result, Reason::invalidArgument, ERROR_INVALID_PARAMETER); return; }
    if (!parentAlive(context)) { refuse(result, Reason::parentUnavailable); return; }
    if (!verify(process, result, false) || protectedTarget(process, result)) return;
    if (command == Command::closeWindows) closeWindows(context, process, result);
    else if (command == Command::terminate) {
        result.flags |= mayHaveApplied;
        if (TerminateProcess(process, ERROR_PROCESS_ABORTED)) result.outcome = Outcome::terminationRequested;
        else { result.outcome = Outcome::error; result.error = lastError(); }
    } else if (command == Command::suspend || command == Command::resume) {
        result.flags |= mayHaveApplied;
        result.ntStatus = command == Command::suspend ? NtSuspendProcess(process) : NtResumeProcess(process);
        if (NT_SUCCESS(result.ntStatus)) result.outcome = Outcome::nativeRequestAccepted;
        else {
            result.outcome = result.ntStatus == STATUS_NOT_SUPPORTED || result.ntStatus == STATUS_NOT_IMPLEMENTED ? Outcome::unsupported : Outcome::error;
            result.reason = Reason::nativeUnavailable; result.error = RtlNtStatusToDosError(result.ntStatus);
        }
    } else if (command == Command::priority || command == Command::affinity) {
        result.flags |= mayHaveApplied;
        const bool applied = command == Command::priority ? SetPriorityClass(process, static_cast<DWORD>(argument)) != FALSE
            : SetProcessAffinityMask(process, static_cast<DWORD_PTR>(argument)) != FALSE;
        if (!applied) { result.outcome = Outcome::error; result.error = lastError(); }
        else {
            metadata(process, result);
            const bool confirmed = command == Command::priority ? (result.flags & priorityAvailable) && result.priority == argument
                : (result.flags & affinityAvailable) && result.group == group && result.processMask == argument;
            result.outcome = confirmed ? Outcome::applied : Outcome::unverified;
            if (confirmed) result.flags |= effectConfirmed;
            else { result.reason = Reason::readbackMismatch; result.error = ERROR_RETRY; }
        }
    } else refuse(result, Reason::invalidArgument, ERROR_INVALID_PARAMETER);
    lifeAfter(process, command, result);
}

void applyAction(const Context& context, Command command, std::uint64_t argument, std::uint32_t group, Result& result)
{
    if (!targetAllowed(context, result)) return;
    const auto pid = result.target.key.pid;
    DWORD access = PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE;
    if (command == Command::terminate) access |= PROCESS_TERMINATE;
    if (command == Command::suspend || command == Command::resume) access |= PROCESS_SUSPEND_RESUME;
    if (command == Command::priority || command == Command::affinity) access |= PROCESS_SET_INFORMATION;
    Handle process(OpenProcess(access, FALSE, pid));
    if (!process) { const auto error = lastError(); refuse(result, error == ERROR_ACCESS_DENIED ? Reason::accessDenied : Reason::identityUnverifiable, error); return; }
    applyHeldAction(context, command, argument, group, process.get(), result);
}
}
