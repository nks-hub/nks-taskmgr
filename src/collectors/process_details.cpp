#include "process_details.hpp"
#include <tlhelp32.h>
#include <algorithm>
#include <array>
#include <limits>
#include <unordered_set>

namespace nks {
namespace {

details::ResultState errorState(DWORD error)
{
    if (error == ERROR_ACCESS_DENIED || error == ERROR_PRIVILEGE_NOT_HELD) return details::ResultState::accessDenied;
    if (error == ERROR_NOT_SUPPORTED || error == ERROR_CALL_NOT_IMPLEMENTED) return details::ResultState::unsupported;
    if (error == ERROR_NOT_FOUND || error == ERROR_INVALID_PARAMETER || error == ERROR_PARTIAL_COPY
        || error == ERROR_BAD_LENGTH || error == ERROR_NO_MORE_FILES || error == ERROR_INSUFFICIENT_BUFFER) return details::ResultState::unavailable;
    return details::ResultState::error;
}

void fail(ProcessDetailsResult& result, DWORD error)
{
    result.state = errorState(error); result.win32Error = error;
}

SampleState metricState(DWORD error)
{
    if (error == ERROR_ACCESS_DENIED || error == ERROR_PRIVILEGE_NOT_HELD) return SampleState::accessDenied;
    if (error == ERROR_NOT_SUPPORTED || error == ERROR_CALL_NOT_IMPLEMENTED) return SampleState::unsupported;
    return SampleState::unavailable;
}

bool verifyTarget(HANDLE process, const ProcessIdentity& expected, ProcessDetailsResult& result)
{
    const auto identity = queryProcessIdentity(process, expected.generationKind);
    if (identity.state != SampleState::valid) {
        result.state = identity.state == SampleState::accessDenied ? details::ResultState::accessDenied
            : identity.state == SampleState::unsupported ? details::ResultState::unsupported : details::ResultState::unavailable;
        return false;
    }
    if (identity.identity != expected) { result.state = details::ResultState::targetChanged; result.win32Error = ERROR_RETRY; return false; }
    const auto wait = WaitForSingleObject(process, 0);
    if (wait == WAIT_TIMEOUT) return true;
    if (wait == WAIT_OBJECT_0) { result.state = details::ResultState::targetChanged; result.win32Error = ERROR_PROCESS_ABORTED; }
    else fail(result, GetLastError());
    return false;
}

details::Architecture architecture(HANDLE process, DWORD& error)
{
    using IsWow64Process2Fn = BOOL (WINAPI*)(HANDLE, USHORT*, USHORT*);
    const auto query = reinterpret_cast<IsWow64Process2Fn>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "IsWow64Process2"));
    if (query) {
        USHORT machine{}, native{};
        if (!query(process, &machine, &native)) { error = GetLastError(); return details::Architecture::unknown; }
        if (native != IMAGE_FILE_MACHINE_AMD64) { error = ERROR_NOT_SUPPORTED; return details::Architecture::arm64; }
        if (machine == IMAGE_FILE_MACHINE_I386) return details::Architecture::x86;
        if (machine == IMAGE_FILE_MACHINE_UNKNOWN || machine == IMAGE_FILE_MACHINE_AMD64) return details::Architecture::x64;
        error = ERROR_NOT_SUPPORTED; return details::Architecture::unknown;
    }
    BOOL wow64{};
    if (!IsWow64Process(process, &wow64)) { error = GetLastError(); return details::Architecture::unknown; }
    return wow64 ? details::Architecture::x86 : details::Architecture::x64;
}

DetailHandle snapshot(DWORD flags, DWORD pid, DWORD& error)
{
    for (unsigned attempt = 0; attempt < 4; ++attempt) {
        DetailHandle result(CreateToolhelp32Snapshot(flags, pid));
        if (result.get() != INVALID_HANDLE_VALUE) return result;
        error = GetLastError();
        if (error != ERROR_BAD_LENGTH) break;
    }
    return {};
}

std::uint64_t ticks(FILETIME value)
{
    return (static_cast<std::uint64_t>(value.dwHighDateTime) << 32) | value.dwLowDateTime;
}

void readThreads(std::uint32_t pid, ProcessDetailsResult& result)
{
    DWORD error = 0;
    auto list = snapshot(TH32CS_SNAPTHREAD, 0, error);
    if (!list) { fail(result, error); return; }
    THREADENTRY32 entry{}; entry.dwSize = sizeof(entry);
    if (!Thread32First(list.get(), &entry)) { fail(result, GetLastError()); return; }
    std::unordered_set<DWORD> ids;
    unsigned visited = 0;
    do {
        if (++visited > 1'048'576) { result.state = details::ResultState::limitExceeded; return; }
        if (entry.th32OwnerProcessID != pid) continue;
        if (result.threads.size() == details::maximumThreads) { result.state = details::ResultState::limitExceeded; return; }
        if (!ids.insert(entry.th32ThreadID).second) { fail(result, ERROR_INVALID_DATA); return; }
        details::Thread thread{};
        thread.threadId = entry.th32ThreadID; thread.ownerPid = pid; thread.basePriority = entry.tpBasePri;
        thread.timesState = thread.priorityState = static_cast<std::uint32_t>(SampleState::unavailable);
        DetailHandle handle(OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ThreadID));
        if (!handle) {
            thread.timesError = thread.priorityError = GetLastError();
            thread.timesState = thread.priorityState = static_cast<std::uint32_t>(metricState(thread.timesError));
        } else {
            const auto owner = GetProcessIdOfThread(handle.get());
            if (owner != pid) {
                thread.timesError = thread.priorityError = owner ? ERROR_RETRY : GetLastError();
                thread.timesState = thread.priorityState = static_cast<std::uint32_t>(metricState(thread.timesError));
                result.flags |= details::partialMetrics; result.threads.push_back(thread); continue;
            }
            FILETIME created{}, exited{}, kernel{}, user{};
            if (GetThreadTimes(handle.get(), &created, &exited, &kernel, &user)) {
                thread.created100ns = ticks(created); thread.kernel100ns = ticks(kernel); thread.user100ns = ticks(user);
                thread.timesState = static_cast<std::uint32_t>(SampleState::valid);
            } else { thread.timesError = GetLastError(); thread.timesState = static_cast<std::uint32_t>(metricState(thread.timesError)); }
            const auto priority = GetThreadPriority(handle.get());
            if (priority != THREAD_PRIORITY_ERROR_RETURN) {
                thread.relativePriority = priority; thread.priorityState = static_cast<std::uint32_t>(SampleState::valid);
            } else { thread.priorityError = GetLastError(); thread.priorityState = static_cast<std::uint32_t>(metricState(thread.priorityError)); }
        }
        if (thread.timesState != static_cast<std::uint32_t>(SampleState::valid)
            || thread.priorityState != static_cast<std::uint32_t>(SampleState::valid)) result.flags |= details::partialMetrics;
        result.threads.push_back(thread);
    } while (Thread32Next(list.get(), &entry));
    error = GetLastError();
    if (error != ERROR_NO_MORE_FILES) { fail(result, error); return; }
    if (result.threads.empty()) { fail(result, ERROR_NO_MORE_FILES); return; }
    result.state = details::ResultState::ok;
}

template<std::size_t Size>
bool copyText(const wchar_t (&text)[Size], std::wstring& result)
{
    const auto end = std::find(text, text + Size, L'\0');
    if (end == text + Size) return false;
    result.assign(text, end); return true;
}

void readModules(std::uint32_t pid, ProcessDetailsResult& result)
{
    DWORD error = 0;
    const DWORD flags = result.architecture == details::Architecture::x86 ? TH32CS_SNAPMODULE32 : TH32CS_SNAPMODULE;
    auto list = snapshot(flags, pid, error);
    if (!list) { fail(result, error); return; }
    MODULEENTRY32W entry{}; entry.dwSize = sizeof(entry);
    if (!Module32FirstW(list.get(), &entry)) { fail(result, GetLastError()); return; }
    std::unordered_set<std::uint64_t> bases;
    do {
        if (result.modules.size() == details::maximumModules) { result.state = details::ResultState::limitExceeded; return; }
        ProcessModuleDetail module;
        module.base = reinterpret_cast<std::uintptr_t>(entry.modBaseAddr); module.imageSize = entry.modBaseSize;
        if (entry.th32ProcessID != pid || !module.base || !module.imageSize || module.imageSize > std::numeric_limits<std::uint64_t>::max() - module.base
            || !bases.insert(module.base).second || !copyText(entry.szModule, module.name) || !copyText(entry.szExePath, module.path)
            || module.name.empty() || module.path.empty()) { fail(result, ERROR_INVALID_DATA); return; }
        if (module.path.size() == std::size(entry.szExePath) - 1) { fail(result, ERROR_INSUFFICIENT_BUFFER); return; }
        if (result.architecture == details::Architecture::x86 && module.base + module.imageSize > (std::uint64_t{1} << 32)) {
            fail(result, ERROR_INVALID_DATA); return;
        }
        result.modules.push_back(std::move(module));
    } while (Module32NextW(list.get(), &entry));
    error = GetLastError();
    if (error != ERROR_NO_MORE_FILES) { fail(result, error); return; }
    if (result.modules.empty()) { fail(result, ERROR_NO_MORE_FILES); return; }
    result.state = details::ResultState::ok;
}

}

ProcessDetailsResult readProcessDetails(const ProcessDetailsRequest& request)
{
    ProcessDetailsResult result;
    if (request.command == details::Command::signature) result.flags |= details::diskFile;
    if (request.command == details::Command::owners) {
        if (request.target.boot.empty() || request.target.pid || request.target.generation
            || (request.target.generationKind != GenerationKind::creationTime && request.target.generationKind != GenerationKind::sequenceNumber)) {
            result.state = details::ResultState::invalidRequest; return result;
        }
        readProcessOwners(request, result);
        if (result.state != details::ResultState::ok) result.owners.clear();
        return result;
    }
    if (request.target.boot.empty() || !request.target.generation || (request.target.generationKind != GenerationKind::creationTime
        && request.target.generationKind != GenerationKind::sequenceNumber)) { result.state = details::ResultState::invalidRequest; return result; }
    DWORD access = PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE;
    if (request.command == details::Command::memory || request.command == details::Command::handles) access |= PROCESS_QUERY_INFORMATION;
    if (request.command == details::Command::handleInfo) access |= PROCESS_DUP_HANDLE;
    DetailHandle process(OpenProcess(access, FALSE, request.target.pid));
    if (!process) { fail(result, GetLastError()); return result; }
    if (!verifyTarget(process.get(), request.target, result)) return result;
    DWORD error = 0; result.architecture = architecture(process.get(), error);
    if (error) { fail(result, error); return result; }
    if (request.command == details::Command::threads) readThreads(request.target.pid, result);
    else if (request.command == details::Command::memory) readProcessMemory(process.get(), result);
    else if (request.command == details::Command::handles) readProcessHandles(process.get(), result);
    else if (request.command == details::Command::handleInfo) readProcessHandleInfo(process.get(), request.handleValue, result);
    else if (request.command == details::Command::modules || request.command == details::Command::signature) {
        readModules(request.target.pid, result);
        if (request.command == details::Command::signature && result.state == details::ResultState::ok) {
            const auto selected = std::find_if(result.modules.begin(), result.modules.end(), [&](const auto& module) {
                return module.base == request.moduleBase && module.path == request.modulePath;
            });
            if (selected == result.modules.end()) { result.state = details::ResultState::targetChanged; result.win32Error = ERROR_NOT_FOUND; }
            else readFileSignatureOnDisk(selected->path, result);
            result.modules.clear();
        }
    } else result.state = details::ResultState::invalidRequest;
    if (!verifyTarget(process.get(), request.target, result) || result.state != details::ResultState::ok) {
        result.threads.clear(); result.modules.clear(); result.signature.reset();
        result.memory.clear(); result.handles.clear(); result.handleInfo.reset();
    }
    return result;
}

}
