#include <phnt_windows.h>
#include <phnt.h>
// PHNT's ULONG alias hides the SDK enum required by the Win32 job APIs.
#undef JOBOBJECTINFOCLASS
#include "core/collector_client.hpp"
#include "core/monitor.hpp"
#include <bcrypt.h>
#include <algorithm>
#include <array>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

namespace {
using Clock = std::chrono::steady_clock;
using namespace std::chrono_literals;
namespace fs = std::filesystem;

void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }

struct Handle {
    HANDLE value{};
    explicit Handle(HANDLE value = nullptr) : value(value) {}
    ~Handle() { reset(); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value(std::exchange(other.value, nullptr)) {}
    void reset() noexcept { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); value = nullptr; }
};

template<class Predicate>
void waitFor(Predicate ready, std::chrono::seconds timeout, const char* message)
{
    const auto deadline = Clock::now() + timeout;
    while (!ready()) {
        require(Clock::now() < deadline, message);
        std::this_thread::sleep_for(25ms);
    }
}

std::uint64_t rawTime(const FILETIME& value) { return (static_cast<std::uint64_t>(value.dwHighDateTime) << 32) | value.dwLowDateTime; }

std::uint64_t createdAt(HANDLE process)
{
    FILETIME created{}, stopped{}, kernel{}, user{};
    require(GetProcessTimes(process, &created, &stopped, &kernel, &user) != FALSE, "Process creation time unavailable");
    return rawTime(created);
}

fs::path imagePath(HANDLE process)
{
    std::wstring path(32768, L'\0'); DWORD length = static_cast<DWORD>(path.size());
    require(QueryFullProcessImageNameW(process, 0, path.data(), &length) != FALSE, "Held process image path unavailable");
    path.resize(length); return path;
}

DWORD activeProcesses(HANDLE job)
{
    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info{};
    require(QueryInformationJobObject(job, static_cast<JOBOBJECTINFOCLASS>(JobObjectBasicAccountingInformation), &info, sizeof(info), nullptr) != FALSE,
        "Own job accounting unavailable");
    return info.ActiveProcesses;
}

struct Lifetime { DWORD pid{}; std::uint64_t created{}, exited{}; };

struct Worker {
    Handle process;
    DWORD pid{};
    std::uint64_t created{};
    Worker(HANDLE value) : process(value), pid(GetProcessId(value)), created(createdAt(value)) {}
    DWORD parentPid() const {
        PROCESS_BASIC_INFORMATION info{}; ULONG used{};
        require(NT_SUCCESS(NtQueryInformationProcess(process.value, ProcessBasicInformation, &info, sizeof(info), &used))
            && used == sizeof(info) && reinterpret_cast<ULONG_PTR>(info.InheritedFromUniqueProcessId) <= std::numeric_limits<DWORD>::max(),
            "Held process parent identity unavailable");
        return static_cast<DWORD>(reinterpret_cast<ULONG_PTR>(info.InheritedFromUniqueProcessId));
    }
    Lifetime lifetime() const {
        FILETIME start{}, stop{}, kernel{}, user{};
        require(GetProcessTimes(process.value, &start, &stop, &kernel, &user) && rawTime(start) == created,
            "Held process lifetime unavailable");
        return {pid, created, rawTime(stop)};
    }
    bool alive() const {
        const auto state = WaitForSingleObject(process.value, 0);
        require(state == WAIT_OBJECT_0 || state == WAIT_TIMEOUT, "Held process liveness unavailable");
        return state == WAIT_TIMEOUT;
    }
    void verify(HANDLE job, const fs::path& expected, DWORD parent) const {
        BOOL member{};
        require(pid != 0 && created != 0 && GetProcessId(process.value) == pid && createdAt(process.value) == created,
            "Held worker lifetime changed");
        require(IsProcessInJob(process.value, job, &member) && member, "Worker is outside the owned test job");
        require(parentPid() == parent, "Worker parent is not the verified owner");
        require(_wcsicmp(imagePath(process.value).c_str(), expected.c_str()) == 0, "Worker image is outside the private fixture");
    }
};

std::vector<DWORD> jobPids(HANDLE job)
{
    struct ProcessIds { DWORD assigned{}, count{}; ULONG_PTR ids[16]{}; } ids;
    require(QueryInformationJobObject(job, static_cast<JOBOBJECTINFOCLASS>(JobObjectBasicProcessIdList), &ids, sizeof(ids), nullptr) != FALSE
        && ids.assigned == ids.count && ids.count <= std::size(ids.ids), "Owned job process list is incomplete");
    std::vector<DWORD> result;
    for (DWORD i = 0; i < ids.count; ++i) {
        require(ids.ids[i] <= std::numeric_limits<DWORD>::max(), "Owned job PID is invalid");
        result.push_back(static_cast<DWORD>(ids.ids[i]));
    }
    return result;
}

void remember(std::vector<Lifetime>& known, const Lifetime& value)
{
    const auto found = std::find_if(known.begin(), known.end(), [&](const auto& item) { return item.pid == value.pid && item.created == value.created; });
    if (found == known.end()) known.push_back(value); else if (value.exited) found->exited = value.exited;
}

struct OwnedProcesses { std::vector<Worker> workers; unsigned consoleHosts{}; };

OwnedProcesses ownedProcesses(HANDLE job, const fs::path& expected, std::vector<Lifetime>& known)
{
    std::array<wchar_t, 32768> directory{};
    const auto length = GetSystemDirectoryW(directory.data(), static_cast<UINT>(directory.size()));
    require(length != 0 && length < directory.size(), "System console host directory unavailable");
    const auto consoleHost = fs::path(directory.data()) / L"conhost.exe";
    std::vector<std::pair<Worker, fs::path>> members;
    std::vector<Lifetime> currentWorkers;
    OwnedProcesses result;
    for (auto pid : jobPids(job)) {
        if (pid == GetCurrentProcessId()) continue;
        const auto handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid);
        if (!handle) {
            const auto error = GetLastError(); const auto current = jobPids(job);
            require(error == ERROR_INVALID_PARAMETER && std::find(current.begin(), current.end(), pid) == current.end(),
                "A process still in the owned job could not be queried");
            continue;
        }
        Worker process(handle); const auto path = imagePath(handle);
        if (_wcsicmp(path.c_str(), expected.c_str()) == 0) {
            process.verify(job, expected, GetCurrentProcessId());
            require(process.created >= createdAt(GetCurrentProcess()), "Worker predates its test parent");
            const bool alive = process.alive();
            const auto lifetime = process.lifetime(); remember(known, lifetime); currentWorkers.push_back(lifetime);
            require(alive || lifetime.exited != 0, "Exited worker has no verified exit time");
            if (alive) {
                const auto terminate = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | SYNCHRONIZE, FALSE, pid);
                require(terminate != nullptr, "Verified worker termination handle unavailable");
                result.workers.emplace_back(terminate);
                result.workers.back().verify(job, expected, GetCurrentProcessId());
                require(result.workers.back().created == process.created, "Worker lifetime changed while acquiring its held handle");
            }
            members.emplace_back(std::move(process), path);
        } else {
            require(_wcsicmp(path.c_str(), consoleHost.c_str()) == 0, "An unexpected image entered the owned test job");
            members.emplace_back(std::move(process), path);
        }
    }
    for (const auto& [host, path] : members) {
        if (_wcsicmp(path.c_str(), expected.c_str()) == 0) continue;
        const auto parent = host.parentPid(); const auto hostLife = host.lifetime();
        const auto matches = [&](const Lifetime& owner) { return owner.pid == parent && owner.created <= hostLife.created
            && (!owner.exited || hostLife.created <= owner.exited); };
        const bool ownParent = parent == GetCurrentProcessId() && hostLife.created >= createdAt(GetCurrentProcess());
        const bool currentParent = std::any_of(currentWorkers.begin(), currentWorkers.end(), matches);
        const bool retiredParent = std::any_of(known.begin(), known.end(), [&](const auto& owner) { return owner.exited && matches(owner); });
        require(ownParent || currentParent || retiredParent, "System console host has no verified owned parent lifetime");
        host.verify(job, path, parent);
        if (host.alive()) ++result.consoleHosts;
    }
    return result;
}

std::vector<Worker> ownWorkers(HANDLE job, const fs::path& expected, std::vector<Lifetime>& known)
{
    auto owned = ownedProcesses(job, expected, known);
    require(owned.workers.size() == 2, "Expected exactly one live process and one live resource worker");
    return std::move(owned.workers);
}

std::vector<Lifetime> waitExited(std::vector<Worker>& workers, std::vector<Lifetime>& known)
{
    std::vector<Lifetime> result;
    for (auto& worker : workers) {
        require(WaitForSingleObject(worker.process.value, 5000) == WAIT_OBJECT_0, "Owned worker did not actually exit");
        const auto lifetime = worker.lifetime();
        require(lifetime.exited != 0, "Terminated worker has no exit time");
        remember(known, lifetime); result.push_back(lifetime);
    }
    workers.clear();
    return result;
}

std::vector<Lifetime> stopWorkers(std::vector<Worker>& workers, HANDLE job, const fs::path& expected, std::vector<Lifetime>& known)
{
    for (auto& worker : workers) {
        worker.verify(job, expected, GetCurrentProcessId());
        require(WaitForSingleObject(worker.process.value, 0) == WAIT_TIMEOUT, "Worker exited before fault injection");
        require(TerminateProcess(worker.process.value, ERROR_OPERATION_ABORTED) != FALSE, "Owned worker termination failed");
    }
    return waitExited(workers, known);
}

void replacements(const std::vector<Lifetime>& before, const std::vector<Worker>& after)
{
    for (const auto& worker : after)
        require(std::none_of(before.begin(), before.end(), [&](const auto& old) { return old.pid == worker.pid && old.created == worker.created; }),
            "Recovery retained a terminated worker lifetime");
}

std::string hashFile(const fs::path& path)
{
    Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    BY_HANDLE_FILE_INFORMATION info{}; LARGE_INTEGER size{};
    require(file.value != INVALID_HANDLE_VALUE && GetFileInformationByHandle(file.value, &info)
        && !(info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY))
        && GetFileSizeEx(file.value, &size) && size.QuadPart > 0 && size.QuadPart <= 16 * 1024 * 1024,
        "Fixture file is missing, redirected or exceeds 16 MiB");
    std::vector<UCHAR> bytes(static_cast<std::size_t>(size.QuadPart)); DWORD at{};
    while (at < bytes.size()) {
        DWORD received{};
        require(ReadFile(file.value, bytes.data() + at, static_cast<DWORD>(bytes.size() - at), &received, nullptr)
            && received != 0, "Fixture file read failed");
        at += received;
    }
    BCRYPT_ALG_HANDLE algorithm{}; std::array<UCHAR, 32> digest{};
    require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0, "SHA-256 unavailable");
    const auto status = BCryptHash(algorithm, nullptr, 0, bytes.data(), static_cast<ULONG>(bytes.size()), digest.data(), static_cast<ULONG>(digest.size()));
    BCryptCloseAlgorithmProvider(algorithm, 0);
    require(status == 0, "Fixture SHA-256 failed");
    constexpr char hex[] = "0123456789ABCDEF";
    std::string result;
    for (auto byte : digest) { result += hex[byte >> 4]; result += hex[byte & 15]; }
    return result;
}

class WorkerFile {
public:
    WorkerFile(fs::path path, std::string digest) : path(std::move(path)), hidden(this->path.wstring() + L".unavailable"), digest(std::move(digest)) {}
    void hide() {
        require(hashFile(path) == digest && !fs::exists(hidden), "Private worker differs or unavailable name is occupied");
        require(MoveFileExW(path.c_str(), hidden.c_str(), 0) != FALSE, "Private worker could not be made unavailable");
        moved = true;
    }
    void restore() {
        if (!moved) return;
        require(hashFile(hidden) == digest && !fs::exists(path), "Worker restoration would overwrite a file or restore changed bytes");
        require(MoveFileExW(hidden.c_str(), path.c_str(), 0) != FALSE, "Private worker restoration failed");
        moved = false;
        require(hashFile(path) == digest, "Restored worker SHA-256 differs");
    }
private:
    fs::path path, hidden;
    std::string digest;
    bool moved{};
};

void directRecovery(HANDLE job, const fs::path& worker, std::vector<Lifetime>& known, std::ostream& log)
{
    std::vector<Worker> recovered;
    {
        nks::CollectorClient processes(nks::collector::Kind::processes), resources(nks::collector::Kind::resources);
        const auto firstProcess = processes.processes({}).measuredAt;
        const auto firstResource = resources.resources({}).measuredAt;
        require(firstProcess != Clock::time_point{} && firstResource != Clock::time_point{}, "Direct initial samples have no measurement time");
        auto handles = ownWorkers(job, worker, known);
        const auto before = stopWorkers(handles, job, worker, known);
        bool processFailed{}, resourceFailed{};
        try { (void)processes.processes({}); } catch (const std::exception&) { processFailed = true; }
        try { (void)resources.resources({}); } catch (const std::exception&) { resourceFailed = true; }
        require(processFailed && resourceFailed, "The first request after actual worker exit did not fail both streams");
        const auto recoveredProcess = processes.processes({}).measuredAt;
        const auto recoveredResource = resources.resources({}).measuredAt;
        require(recoveredProcess > firstProcess && recoveredResource > firstResource, "Direct retry did not collect two fresh samples");
        recovered = ownWorkers(job, worker, known);
        replacements(before, recovered);
        log << "direct immediateAfterExitFailure=True;retryBothFresh=True\n" << std::flush;
    }
    waitExited(recovered, known);
    waitFor([&] { return ownedProcesses(job, worker, known).workers.empty(); }, 5s, "Direct client destruction left live owned workers");
}

void hungRecovery(HANDLE job, const fs::path& worker, std::vector<Lifetime>& known, std::ostream& log)
{
    std::vector<Worker> recovered;
    {
        nks::CollectorClient processes(nks::collector::Kind::processes);
        const auto first = processes.processes({}).measuredAt;
        auto owned = ownedProcesses(job, worker, known);
        require(owned.workers.size() == 1, "Expected exactly one live process worker before the hang");
        const auto& frozen = owned.workers.front();
        Handle suspend(OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, frozen.pid));
        require(suspend.value && createdAt(suspend.value) == frozen.created, "Held worker suspend handle unavailable");
        require(NT_SUCCESS(NtSuspendProcess(suspend.value)), "Owned worker could not be suspended");
        const auto started = Clock::now();
        std::string error;
        try { (void)processes.processes({}); } catch (const std::exception& failure) { error = failure.what(); }
        const auto waited = std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - started);
        require(error == "Collector operation timed out", "A suspended worker did not end the request with a timeout");
        require(waited >= 4900ms && waited < 6500ms, "The hung worker request did not end at its 5 s deadline");
        require(WaitForSingleObject(frozen.process.value, 5000) == WAIT_OBJECT_0, "The timed out worker was not terminated");
        remember(known, frozen.lifetime());
        const auto retryStarted = Clock::now();
        Clock::time_point fresh{};
        waitFor([&] { try { fresh = processes.processes({}).measuredAt; return true; } catch (const std::exception&) { return false; } },
            5s, "No fresh process snapshot after the hung worker was replaced");
        const auto retry = std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - retryStarted);
        require(fresh > first, "The replacement worker returned a stale snapshot");
        auto after = ownedProcesses(job, worker, known);
        require(after.workers.size() == 1 && (after.workers.front().pid != frozen.pid || after.workers.front().created != frozen.created),
            "The hung worker was not replaced by a new lifetime");
        recovered = std::move(after.workers);
        log << "hung timeoutMs=" << waited.count() << ";terminated=True;retryMs=" << retry.count() << ";fresh=True\n" << std::flush;
    }
    waitExited(recovered, known);
    waitFor([&] { return ownedProcesses(job, worker, known).workers.empty(); }, 5s, "Hung recovery client destruction left live owned workers");
}

struct Samples {
    Clock::time_point processTime{}, resourceTime{};
    std::wstring processError, resourceError;
    bool cpuValid{};
    void read(nks::Monitor& monitor) {
        if (auto value = monitor.takeProcesses(processError)) { processTime = value->measuredAt; cpuValid = value->systemCpuPercent.valid(); }
        if (auto value = monitor.takeResources(resourceError)) resourceTime = value->measuredAt;
    }
};

void monitorRecovery(HANDLE job, const fs::path& worker, const std::string& digest, std::vector<Lifetime>& known, std::ostream& log)
{
    nks::Monitor monitor;
    require(monitor.interval() == 1000, "Recovery test changed the one-second production cadence");
    monitor.start(); Samples samples;
    waitFor([&] { samples.read(monitor); return samples.cpuValid && samples.resourceTime != Clock::time_point{}
        && samples.processError.empty() && samples.resourceError.empty(); }, 12s, "Initial monitor streams did not become fresh");
    monitor.setPaused(true);
    // A request already in progress has a 5 s deadline before the pause takes effect.
    std::this_thread::sleep_for(6s);
    samples.read(monitor);
    const auto processTime = samples.processTime, resourceTime = samples.resourceTime;
    auto handles = ownWorkers(job, worker, known);
    const auto before = stopWorkers(handles, job, worker, known);
    WorkerFile file(worker, digest);
    try {
        file.hide(); monitor.setPaused(false);
        waitFor([&] { samples.read(monitor); return !samples.processError.empty() && !samples.resourceError.empty(); }, 8s,
            "Both monitor streams did not report collection failure");
        const auto holdUntil = Clock::now() + 4s;
        while (Clock::now() < holdUntil) {
            samples.read(monitor);
            require(samples.processTime == processTime && samples.resourceTime == resourceTime
                && !samples.processError.empty() && !samples.resourceError.empty(), "Unavailable workers published fresh data or hid collection errors");
            std::this_thread::sleep_for(25ms);
        }
        const auto remaining = ownedProcesses(job, worker, known);
        require(remaining.workers.empty(), "Unavailable worker image left a live collector");
        log << "monitor intervalMs=1000;bothErrors=True;noFreshMetricsFor4s=True;verifiedConsoleHosts=" << remaining.consoleHosts
            << ";processNs=" << processTime.time_since_epoch().count() << ";resourceNs=" << resourceTime.time_since_epoch().count()
            << ";processErrorChars=" << samples.processError.size() << ";resourceErrorChars=" << samples.resourceError.size() << '\n' << std::flush;
        file.restore();
        waitFor([&] { samples.read(monitor); return samples.processTime > processTime && samples.resourceTime > resourceTime
            && samples.cpuValid && samples.processError.empty() && samples.resourceError.empty(); }, 12s,
            "Monitor retry did not recover both clocks and clear collection errors");
        auto recovered = ownWorkers(job, worker, known);
        replacements(before, recovered);
        monitor.stop();
        waitExited(recovered, known);
        waitFor([&] { return ownedProcesses(job, worker, known).workers.empty(); }, 5s, "Normal monitor stop left live owned workers");
        require(hashFile(worker) == digest, "Normal stop changed the restored worker");
        log << "monitor bothClocksRecovered=True;errorsCleared=True;newWorkers=True;normalStop=True;noOwnedWorkers=True"
            << ";processNs=" << samples.processTime.time_since_epoch().count() << ";resourceNs=" << samples.resourceTime.time_since_epoch().count() << '\n' << std::flush;
    } catch (const std::exception&) {
        monitor.stop(); file.restore(); throw;
    }
}

std::wstring commandLine(HANDLE process)
{
    std::vector<std::byte> buffer(65536); ULONG used{};
    require(NT_SUCCESS(NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer.data(), static_cast<ULONG>(buffer.size()), &used)),
        "Worker command line unavailable");
    const auto text = reinterpret_cast<const UNICODE_STRING*>(buffer.data());
    return {text->Buffer, text->Length / sizeof(wchar_t)};
}

// A hung process provider must not stall the resource stream, and the process stream must recover by itself.
void monitorSlowProvider(HANDLE job, const fs::path& worker, std::vector<Lifetime>& known, std::ostream& log)
{
    nks::Monitor monitor;
    monitor.start(); Samples samples;
    try {
        waitFor([&] { samples.read(monitor); return samples.cpuValid && samples.resourceTime != Clock::time_point{}
            && samples.processError.empty() && samples.resourceError.empty(); }, 12s, "Initial monitor streams did not become fresh");
        auto handles = ownWorkers(job, worker, known);
        const auto frozen = std::find_if(handles.begin(), handles.end(),
            [](const auto& item) { return commandLine(item.process.value).ends_with(L" --processes"); });
        require(frozen != handles.end(), "No live process worker to suspend");
        Handle suspend(OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, frozen->pid));
        require(suspend.value && createdAt(suspend.value) == frozen->created, "Held process worker suspend handle unavailable");
        require(NT_SUCCESS(NtSuspendProcess(suspend.value)), "Process worker could not be suspended");
        const auto suspended = Clock::now();
        std::this_thread::sleep_for(300ms);
        samples.read(monitor);
        const auto stalled = samples.processTime;
        auto resourceTime = samples.resourceTime; unsigned resourceSamples{};
        while (Clock::now() - suspended < 4s) {
            samples.read(monitor);
            require(samples.processTime == stalled, "A suspended process worker published a fresh snapshot");
            if (samples.resourceTime > resourceTime) { resourceTime = samples.resourceTime; ++resourceSamples; }
            std::this_thread::sleep_for(25ms);
        }
        require(resourceSamples >= 2, "The resource stream stalled while only the process provider was hung");
        waitFor([&] { samples.read(monitor); return samples.processTime > stalled && samples.cpuValid && samples.processError.empty(); }, 8s,
            "The process stream did not recover after its hung worker timed out");
        const auto recoveredAfter = std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - suspended);
        require(WaitForSingleObject(frozen->process.value, 5000) == WAIT_OBJECT_0, "The hung process worker was not terminated");
        remember(known, frozen->lifetime());
        auto recovered = ownWorkers(job, worker, known);
        replacements({frozen->lifetime()}, recovered);
        log << "slow provider resourceSamplesIn4s=" << resourceSamples << ";processStalled=True;processRecoveredMs=" << recoveredAfter.count()
            << ";hungWorkerTerminated=True\n" << std::flush;
        monitor.stop();
        waitExited(recovered, known);
        waitFor([&] { return ownedProcesses(job, worker, known).workers.empty(); }, 5s, "Slow provider monitor stop left live owned workers");
    } catch (const std::exception&) { monitor.stop(); throw; }
}

int child(HANDLE job, const std::string& digest)
{
    const auto self = imagePath(GetCurrentProcess());
    std::ofstream log(self.parent_path() / L"collector-recovery-result.txt");
    try {
        require(log.is_open(), "Private child log unavailable");
        BOOL member{};
        require(IsProcessInJob(GetCurrentProcess(), job, &member) != FALSE, "Private child job membership query failed");
        require(member != FALSE, "Test child is outside its inherited private job");
        const auto worker = self.parent_path() / L"nks-collector-worker.exe";
        require(hashFile(worker) == digest, "Child worker differs from the parent's verified copy");
        std::vector<Lifetime> known;
        const auto baseline = ownedProcesses(job, worker, known);
        require(baseline.workers.empty(), "Collector workers were running before the recovery test started");
        log << "workerSha256=" << digest << ";verifiedBaselineConsoleHosts=" << baseline.consoleHosts << '\n' << std::flush;
        directRecovery(job, worker, known, log);
        hungRecovery(job, worker, known, log);
        monitorSlowProvider(job, worker, known, log);
        monitorRecovery(job, worker, digest, known, log);
        log << "Collector recovery passed\n";
        return 0;
    } catch (const std::exception& error) { log << "FAIL: " << error.what() << '\n'; return 1; }
}

void copyVerified(const fs::path& source, const fs::path& destination)
{
    const auto digest = hashFile(source);
    require(CopyFileW(source.c_str(), destination.c_str(), TRUE) != FALSE && hashFile(destination) == digest,
        "Private fixture copy failed or changed bytes");
}

int parent()
{
    const auto self = imagePath(GetCurrentProcess());
    std::array<wchar_t, 32768> temporary{};
    const auto length = GetTempPathW(static_cast<DWORD>(temporary.size()), temporary.data());
    require(length != 0 && length < temporary.size(), "Private fixture parent unavailable");
    std::array<UCHAR, 16> nonce{};
    require(BCryptGenRandom(nullptr, nonce.data(), static_cast<ULONG>(nonce.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0,
        "Private fixture randomness unavailable");
    constexpr wchar_t hex[] = L"0123456789abcdef";
    std::wstring name = L"nks-collector-recovery-";
    for (auto byte : nonce) { name += hex[byte >> 4]; name += hex[byte & 15]; }
    const auto fixture = fs::path(temporary.data()) / name;
    require(CreateDirectoryW(fixture.c_str(), nullptr) != FALSE, "Private fixture directory already exists or could not be created");
    const auto fixtureText = fixture.u8string();
    std::cout << "Fixture: " << std::string(fixtureText.begin(), fixtureText.end()) << '\n' << std::flush;
    const auto executable = fixture / self.filename(), worker = fixture / L"nks-collector-worker.exe";
    copyVerified(self, executable); copyVerified(self.parent_path() / worker.filename(), worker);
    for (const auto* dll : {L"concrt140.dll", L"msvcp140.dll", L"msvcp140_1.dll", L"msvcp140_2.dll", L"msvcp140_atomic_wait.dll",
        L"msvcp140_codecvt_ids.dll", L"vccorlib140.dll", L"vcruntime140.dll", L"vcruntime140_1.dll", L"vcruntime140_threads.dll"}) {
        const auto source = self.parent_path() / dll;
        if (fs::exists(source)) copyVerified(source, fixture / dll);
    }
    const auto digest = hashFile(worker);
    SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
    Handle ownerJob(CreateJobObjectW(nullptr, nullptr));
    Handle job(CreateJobObjectW(&security, nullptr));
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    require(ownerJob.value && job.value
        && SetInformationJobObject(ownerJob.value, static_cast<JOBOBJECTINFOCLASS>(JobObjectExtendedLimitInformation), &limits, sizeof(limits))
        && SetInformationJobObject(job.value, static_cast<JOBOBJECTINFOCLASS>(JobObjectExtendedLimitInformation), &limits, sizeof(limits)), "Private test job setup failed");
    SIZE_T attributeBytes{}; InitializeProcThreadAttributeList(nullptr, 2, 0, &attributeBytes);
    std::vector<std::byte> storage(attributeBytes);
    const auto attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
    require(InitializeProcThreadAttributeList(attributes, 2, 0, &attributeBytes) != FALSE, "Child attributes unavailable");
    STARTUPINFOEXW startup{}; startup.StartupInfo.cb = sizeof(startup); startup.lpAttributeList = attributes;
    PROCESS_INFORMATION started{};
    auto command = L"\"" + executable.wstring() + L"\" --child " + std::to_wstring(reinterpret_cast<ULONG_PTR>(job.value))
        + L" " + std::wstring(digest.begin(), digest.end());
    HANDLE jobs[]{ownerJob.value, job.value};
    // Atomic job assignment avoids leaving an unowned suspended process if creation fails.
    const bool configured = UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, &job.value, sizeof(job.value), nullptr, nullptr)
        && UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, jobs, sizeof(jobs), nullptr, nullptr);
    const bool launched = configured && CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
        CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT, nullptr, fixture.c_str(), &startup.StartupInfo, &started);
    DeleteProcThreadAttributeList(attributes);
    require(launched, "Private test child creation failed");
    Worker process(started.hProcess); Handle thread(started.hThread);
    process.verify(job.value, executable, GetCurrentProcessId());
    require(ResumeThread(thread.value) == 1, "Private test child could not resume");
    require(WaitForSingleObject(process.process.value, 75000) == WAIT_OBJECT_0, "Collector recovery child exceeded 75 seconds");
    DWORD result{};
    require(GetExitCodeProcess(process.process.value, &result) != FALSE, "Private test child exit unavailable");
    thread.reset(); process.process.reset();
    waitFor([&] { return activeProcesses(ownerJob.value) == 0 && activeProcesses(job.value) == 0; }, 5s,
        "Recovery left processes in its private jobs");
    std::ifstream log(fixture / L"collector-recovery-result.txt"); std::cout << log.rdbuf();
    require(result == 0, "Collector recovery child failed");
    require(hashFile(worker) == digest && !fs::exists(worker.wstring() + L".unavailable"), "Private worker was not restored");
    std::cout << "Owned job cleanup passed; no runtime files deleted\n";
    return 0;
}
}

int wmain(int argc, wchar_t** argv)
{
    try {
        if (argc == 4 && std::wstring_view(argv[1]) == L"--child") {
            const auto job = reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(std::stoull(argv[2])));
            const std::wstring encoded(argv[3]);
            require(encoded.size() == 64, "Invalid private worker digest");
            std::string digest;
            for (auto value : encoded) {
                require((value >= L'0' && value <= L'9') || (value >= L'A' && value <= L'F'), "Invalid private worker digest character");
                digest += static_cast<char>(value);
            }
            return child(job, digest);
        }
        require(argc == 1, "Unexpected recovery test arguments");
        return parent();
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
