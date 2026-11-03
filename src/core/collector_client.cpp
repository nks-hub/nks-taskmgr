#include "collector_client.hpp"
#include "collector_worker/service_codec.hpp"
#include <windows.h>
#include <bcrypt.h>
#include <sddl.h>
#include <array>
#include <algorithm>
#include <system_error>

namespace nks {
namespace {

class Handle {
public:
    Handle() = default;
    explicit Handle(HANDLE value) : value(value) {}
    ~Handle() { reset(); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    HANDLE get() const { return value; }
    void reset(HANDLE replacement = nullptr) noexcept {
        if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value);
        value = replacement;
    }
private:
    HANDLE value{};
};

[[noreturn]] void winError(const char* action) {
    throw std::system_error(static_cast<int>(GetLastError()), std::system_category(), action);
}

std::wstring workerPath()
{
    static const int moduleAnchor{};
    HMODULE module{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&moduleAnchor), &module)) winError("Locate collector module");
    std::wstring path(32768, L'\0');
    const auto length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) throw std::runtime_error("Collector module path exceeds limit");
    path.resize(length);
    const auto slash = path.find_last_of(L"\\/");
    if (slash == std::wstring::npos) throw std::runtime_error("Collector module directory is unavailable");
    path.resize(slash + 1); path += L"nks-collector-worker.exe"; return path;
}

class PipeSecurity {
public:
    PipeSecurity() {
        HANDLE rawToken{};
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &rawToken)) winError("Read collector token");
        Handle token(rawToken);
        DWORD needed{}; GetTokenInformation(token.get(), TokenUser, nullptr, 0, &needed);
        if (!needed || needed > 65536) throw std::runtime_error("Collector token size is invalid");
        std::vector<std::byte> buffer(needed);
        if (!GetTokenInformation(token.get(), TokenUser, buffer.data(), needed, &needed)) winError("Read collector token user");
        LPWSTR sid{};
        if (!ConvertSidToStringSidW(reinterpret_cast<const TOKEN_USER*>(buffer.data())->User.Sid, &sid)) winError("Read collector user SID");
        std::wstring descriptor;
        try { descriptor = L"D:P(A;;GA;;;SY)(A;;GA;;;" + std::wstring(sid) + L")"; }
        catch (...) { LocalFree(sid); throw; }
        LocalFree(sid);
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptor.c_str(), SDDL_REVISION_1, &security, nullptr))
            winError("Create collector pipe security");
    }
    ~PipeSecurity() { if (security) LocalFree(security); }
    SECURITY_ATTRIBUTES attributes() const { return {sizeof(SECURITY_ATTRIBUTES), security, FALSE}; }
private:
    PSECURITY_DESCRIPTOR security{};
};

std::wstring pipeName()
{
    std::array<unsigned char, 16> nonce{};
    if (BCryptGenRandom(nullptr, nonce.data(), static_cast<ULONG>(nonce.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG) < 0)
        throw std::runtime_error("Collector pipe randomness is unavailable");
    std::wstring name = L"\\\\.\\pipe\\NksTaskMgr.Collector.";
    constexpr wchar_t digits[] = L"0123456789abcdef";
    for (auto byte : nonce) { name += digits[byte >> 4]; name += digits[byte & 15]; }
    return name;
}

class StartupAttributes {
public:
    StartupAttributes(HANDLE input, HANDLE error) {
        SIZE_T size{}; InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
        storage.resize(size);
        list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
        if (!InitializeProcThreadAttributeList(list, 1, 0, &size)) { list = nullptr; winError("Initialize collector handle list"); }
        handles = {input, error};
        if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles.data(), sizeof(handles), nullptr, nullptr)) {
            const auto code = GetLastError(); DeleteProcThreadAttributeList(list); list = nullptr; SetLastError(code);
            winError("Set collector handle list");
        }
    }
    ~StartupAttributes() { if (list) DeleteProcThreadAttributeList(list); }
    LPPROC_THREAD_ATTRIBUTE_LIST get() const { return list; }
private:
    std::vector<std::byte> storage;
    std::array<HANDLE, 2> handles{};
    LPPROC_THREAD_ATTRIBUTE_LIST list{};
};

void stopCheck(std::stop_token stop) {
    if (stop.stop_requested()) throw std::runtime_error("Collector request cancelled");
}

}

class CollectorClient::Connection {
public:
    Connection(collector::Kind kind, std::stop_token stop) {
        stopCheck(stop);
        cancellation.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!cancellation.get()) winError("Create collector cancellation event");
        completion.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!completion.get()) winError("Create collector transfer event");
        PipeSecurity security; auto attributes = security.attributes(); const auto name = pipeName();
        pipe.reset(CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 65536, 65536, 0, &attributes));
        if (pipe.get() == INVALID_HANDLE_VALUE) winError("Create collector pipe");
        Handle connected(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!connected.get()) winError("Create collector connection event");
        OVERLAPPED operation{}; operation.hEvent = connected.get();
        const bool pending = !ConnectNamedPipe(pipe.get(), &operation);
        if (pending && GetLastError() != ERROR_IO_PENDING) winError("Connect collector pipe");
        // Connect our own endpoint before starting the worker. No external client is accepted.
        Handle childPipe(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr));
        if (childPipe.get() == INVALID_HANDLE_VALUE) {
            const auto code = GetLastError(); cancel(operation); SetLastError(code); winError("Open collector pipe endpoint");
        }
        DWORD transferred{};
        if (pending && !GetOverlappedResult(pipe.get(), &operation, &transferred, TRUE)) winError("Complete collector pipe connection");
        if (!SetHandleInformation(childPipe.get(), HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT)) winError("Inherit collector pipe");
        SECURITY_ATTRIBUTES inherited{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
        Handle error(CreateFileW(L"NUL", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, &inherited, OPEN_EXISTING, 0, nullptr));
        if (error.get() == INVALID_HANDLE_VALUE) winError("Create collector error endpoint");
        job.reset(CreateJobObjectW(nullptr, nullptr));
        if (!job.get()) winError("Create collector job");
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_ACTIVE_PROCESS | JOB_OBJECT_LIMIT_PROCESS_MEMORY;
        limits.BasicLimitInformation.ActiveProcessLimit = 1; limits.ProcessMemoryLimit = 256ull * 1024 * 1024;
        if (!SetInformationJobObject(job.get(), JobObjectExtendedLimitInformation, &limits, sizeof(limits))) winError("Limit collector job");
        StartupAttributes inheritedHandles(childPipe.get(), error.get());
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = sizeof(startup); startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = childPipe.get(); startup.StartupInfo.hStdOutput = childPipe.get();
        startup.StartupInfo.hStdError = error.get(); startup.lpAttributeList = inheritedHandles.get();
        const auto executable = workerPath();
        const auto mode = kind == collector::Kind::processes ? L"--processes" : kind == collector::Kind::resources ? L"--resources" : L"--services";
        auto command = L"\"" + executable + L"\" " + mode;
        PROCESS_INFORMATION info{};
        stopCheck(stop);
        if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
            CREATE_SUSPENDED | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr, &startup.StartupInfo, &info))
            winError("Start collector worker");
        process.reset(info.hProcess); Handle thread(info.hThread);
        if (!AssignProcessToJobObject(job.get(), process.get())) {
            const auto code = GetLastError(); TerminateProcess(process.get(), 1); SetLastError(code); winError("Assign collector worker job");
        }
        if (ResumeThread(thread.get()) == static_cast<DWORD>(-1)) {
            const auto code = GetLastError(); TerminateJobObject(job.get(), 1); SetLastError(code); winError("Resume collector worker");
        }
    }

    std::vector<std::byte> request(const collector::Header& request, std::span<const std::byte> body, std::stop_token stop) {
        const auto deadline = GetTickCount64() + 5000;
        if (!ResetEvent(cancellation.get())) winError("Reset collector cancellation event");
        std::stop_callback callback(stop, [&] { SetEvent(cancellation.get()); });
        auto header = collector::encodeHeader(request);
        transfer(header, true, deadline, cancellation.get());
        if (!body.empty()) {
            std::vector<std::byte> copy(body.begin(), body.end());
            transfer(copy, true, deadline, cancellation.get());
        }
        header.resize(collector::headerBytes);
        transfer(header, false, deadline, cancellation.get());
        const auto response = collector::decodeHeader(header);
        if (response.kind != request.kind || response.requestId != request.requestId)
            throw std::runtime_error("Collector response identity mismatch");
        std::vector<std::byte> payload(response.payloadBytes);
        transfer(payload, false, deadline, cancellation.get());
        stopCheck(stop);
        if (response.status) {
            collector::Reader reader(payload); const auto message = reader.text(); reader.finished();
            std::string text; text.reserve(message.size());
            for (auto ch : message) text += ch >= 32 && ch < 127 ? static_cast<char>(ch) : '?';
            throw std::runtime_error(text.empty() ? "Collector failed" : text);
        }
        return payload;
    }
    void stop() noexcept {
        retiredState = true;
        job.reset(); pipe.reset();
    }
    bool retired() const noexcept { return retiredState; }
    bool exited() const noexcept { return WaitForSingleObject(process.get(), 0) == WAIT_OBJECT_0; }
private:
    void cancel(OVERLAPPED& operation) noexcept {
        CancelIoEx(pipe.get(), &operation);
        DWORD ignored{}; GetOverlappedResult(pipe.get(), &operation, &ignored, TRUE);
    }
    void transfer(std::span<std::byte> bytes, bool write, ULONGLONG deadline, HANDLE stopped) {
        std::size_t at{};
        while (at < bytes.size()) {
            if (WaitForSingleObject(stopped, 0) == WAIT_OBJECT_0) throw std::runtime_error("Collector request cancelled");
            const auto now = GetTickCount64();
            if (now >= deadline) throw std::runtime_error("Collector operation timed out");
            OVERLAPPED operation{}; operation.hEvent = completion.get(); ResetEvent(completion.get());
            const auto size = static_cast<DWORD>((std::min)(bytes.size() - at, std::size_t{65536}));
            DWORD transferred{};
            const bool ready = write ? WriteFile(pipe.get(), bytes.data() + at, size, &transferred, &operation) != FALSE
                : ReadFile(pipe.get(), bytes.data() + at, size, &transferred, &operation) != FALSE;
            if (!ready) {
                if (GetLastError() != ERROR_IO_PENDING) winError("Collector pipe transfer");
                const HANDLE events[]{completion.get(), stopped, process.get()};
                const auto waited = WaitForMultipleObjects(3, events, FALSE, static_cast<DWORD>(deadline - now));
                if (waited != WAIT_OBJECT_0) {
                    const auto code = GetLastError();
                    cancel(operation);
                    if (waited == WAIT_OBJECT_0 + 1) throw std::runtime_error("Collector request cancelled");
                    if (waited == WAIT_OBJECT_0 + 2) throw std::runtime_error("Collector worker exited");
                    if (waited == WAIT_TIMEOUT) throw std::runtime_error("Collector operation timed out");
                    SetLastError(code); winError("Wait for collector response");
                }
                if (!GetOverlappedResult(pipe.get(), &operation, &transferred, FALSE)) winError("Read collector response");
            }
            if (!transferred) throw std::runtime_error("Collector pipe closed");
            at += transferred;
        }
    }
    // Closing the job terminates only this worker, even if its provider is blocked.
    Handle cancellation, completion, pipe, process, job;
    bool retiredState{};
};

CollectorClient::CollectorClient(collector::Kind kind) : kind(kind) {}
CollectorClient::~CollectorClient() = default;

std::vector<std::byte> CollectorClient::request(std::stop_token stop, collector::Kind command, std::span<const std::byte> payload)
{
    try {
        stopCheck(stop);
        if (connection && connection->retired()) {
            if (!connection->exited()) throw std::runtime_error("Previous collector worker is still terminating");
            connection.reset();
        }
        if (!connection) connection = std::make_unique<Connection>(kind, stop);
        if (++requestId == 0) ++requestId;
        if (payload.size() > 65538) throw std::length_error("Collector request exceeds limit");
        return connection->request({command, 0, requestId, static_cast<std::uint32_t>(payload.size())}, payload, stop);
    } catch (...) { if (connection) connection->stop(); throw; }
}

ProcessSnapshot CollectorClient::processes(std::stop_token stop)
{
    if (kind != collector::Kind::processes) throw std::logic_error("Wrong collector kind");
    try { return collector::decodeProcesses(request(stop, kind)); }
    catch (...) { if (connection) connection->stop(); throw; }
}

ResourceSnapshot CollectorClient::resources(std::stop_token stop, bool cpuSensors)
{
    if (kind != collector::Kind::resources) throw std::logic_error("Wrong collector kind");
    collector::Writer body; body.number<std::uint32_t>(cpuSensors ? 1 : 0);
    try { return collector::decodeResources(request(stop, kind, body.bytes)); }
    catch (...) { if (connection) connection->stop(); throw; }
}

ServiceSnapshot CollectorClient::services(std::stop_token stop)
{
    if (kind != collector::Kind::services) throw std::logic_error("Wrong collector kind");
    try { return collector::decodeServices(request(stop, kind)); }
    catch (...) { if (connection) connection->stop(); throw; }
}

ServiceDetail CollectorClient::detail(const std::wstring& name, std::stop_token stop)
{
    if (kind != collector::Kind::services || name.empty()) throw std::logic_error("Invalid service detail request");
    collector::Writer body; body.text(name);
    try {
        auto result = collector::decodeServiceDetail(request(stop, collector::Kind::serviceDetail, body.bytes));
        if (!serviceNameEqual(result.name, name)) throw std::runtime_error("Collector service identity mismatch");
        return result;
    }
    catch (...) { if (connection) connection->stop(); throw; }
}

}
