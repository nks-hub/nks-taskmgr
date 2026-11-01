#include "interop/events_api.h"
#include <windows.h>
#include <algorithm>
#include <array>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <iomanip>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>

namespace {
using Clock = std::chrono::steady_clock;
using namespace std::chrono_literals;
constexpr DWORD childExit = 42;

void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }

struct Handle {
    HANDLE value{};
    ~Handle() { if (value) CloseHandle(value); }
    Handle() = default;
    explicit Handle(HANDLE handle) : value(handle) {}
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};

bool elevated()
{
    Handle token;
    require(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token.value) != FALSE, "Own token unavailable");
    TOKEN_ELEVATION elevation{}; DWORD used{};
    require(GetTokenInformation(token.value, TokenElevation, &elevation, sizeof(elevation), &used) != FALSE,
        "Own token elevation unavailable");
    return elevation.TokenIsElevated != 0;
}

class Child {
public:
    Child() {
        try {
            job.value = CreateJobObjectW(nullptr, nullptr);
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            require(job.value && SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits)),
                "Own child job could not be configured");
            std::array<wchar_t, 32768> path{};
            const auto length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
            require(length > 0 && length < path.size(), "Own executable path unavailable");
            auto command = L"\"" + std::wstring(path.data(), length) + L"\" --child";
            STARTUPINFOW startup{}; startup.cb = sizeof(startup);
            PROCESS_INFORMATION created{};
            require(CreateProcessW(path.data(), command.data(), nullptr, nullptr, FALSE,
                CREATE_NO_WINDOW | CREATE_SUSPENDED, nullptr, nullptr, &startup, &created) != FALSE, "Own suspended child did not start");
            process.value = created.hProcess; thread.value = created.hThread; pid = created.dwProcessId;
            require(AssignProcessToJobObject(job.value, process.value) != FALSE, "Own child job assignment failed");
            FILETIME start{}, stop{}, kernel{}, user{};
            require(GetProcessTimes(process.value, &start, &stop, &kernel, &user) != FALSE, "Own child creation FILETIME unavailable");
            std::memcpy(&created100ns, &start, sizeof(created100ns));
            require(created100ns != 0 && GetProcessId(process.value) == pid, "Own suspended child identity was incomplete");
            require(nks_process_query_identity(process.value, NKS_GENERATION_CREATION_TIME,
                sizeof(identity), &identity) == NKS_OK && identity.state == NKS_SAMPLE_VALID
                && identity.generation_kind == NKS_GENERATION_CREATION_TIME && identity.pid == pid
                && identity.generation == created100ns && (identity.boot_low || identity.boot_high) && identity.reserved == 0,
                "Public held-handle identity disagreed with the independently captured child FILETIME");
        } catch (...) {
            if (process.value && WaitForSingleObject(process.value, 0) == WAIT_TIMEOUT) {
                TerminateProcess(process.value, ERROR_OPERATION_ABORTED);
                WaitForSingleObject(process.value, 2000);
            }
            throw;
        }
    }
    void resumeAndWait() {
        require(ResumeThread(thread.value) == 1, "Own child was not suspended exactly once");
        require(WaitForSingleObject(process.value, 3000) == WAIT_OBJECT_0, "Own short child did not exit within three seconds");
        DWORD code{};
        require(GetExitCodeProcess(process.value, &code) && code == childExit, "Own child exit code differed from the fixture");
    }
    DWORD pid{};
    std::uint64_t created100ns{};
    NksProcessIdentityData identity{};
private:
    Handle job, process, thread;
};

struct Frame {
    explicit Frame(NksEventMonitor* monitor) {
        NksEventFrame* raw{};
        const auto result = nks_events_read(monitor, &raw);
        value.reset(raw);
        require(result == NKS_OK && value, "Public event read failed");
        view = nks_events_frame_view(value.get());
        require(view && view->abi_version == 1 && view->state <= NKS_EVENTS_CLEANUP_FAILED
            && view->boot_state <= NKS_SAMPLE_GAP && view->reserved == 0 && view->reserved2 == 0
            && (view->flags & ~(NKS_EVENTS_RESYNC_REQUIRED | NKS_EVENTS_CLEANUP_OUTSTANDING)) == 0
            && view->event_count <= 512 && (!view->event_count || view->events) && view->qpc_frequency >= 0,
            "Public event frame violated its ABI contract");
        require(nks_events_frame_get_traffic(value.get(), sizeof(traffic), &traffic) == NKS_OK
            && traffic.abi_version == 1 && traffic.sample_count <= 256
            && (!traffic.sample_count || traffic.samples) && traffic.budget_limit == 8 * 1024 * 1024
            && traffic.budget_bytes_used <= traffic.budget_limit, "Public traffic frame violated its bounded contract");
    }
    std::unique_ptr<NksEventFrame, decltype(&nks_events_frame_release)> value{nullptr, nks_events_frame_release};
    const NksEventFrameView* view{};
    NksProcessTrafficView traffic{};
};

struct Capture {
    NksProcessIdentityData target{};
    NksProcessEvent start{}, stop{};
    std::uint64_t starts{}, stops{}, wrongIdentity{};
    std::int64_t frequency{};
    bool conflict{};

    void observe(const NksEventFrameView& frame) {
        if (!target.pid) return;
        for (std::uint32_t i = 0; i < frame.event_count; ++i) {
            const auto& event = frame.events[i];
            if (event.pid != target.pid) continue;
            const bool verified = frame.boot_state == NKS_SAMPLE_VALID
                && frame.boot_low == target.boot_low && frame.boot_high == target.boot_high
                && event.identity_state == NKS_SAMPLE_VALID && event.generation_kind == NKS_GENERATION_CREATION_TIME
                && event.generation == target.generation && event.sequence != 0 && event.timestamp_qpc >= 0
                && event.observed_at_ns > 0 && event.reserved == 0;
            if (!verified) { ++wrongIdentity; continue; }
            if (frame.qpc_frequency <= 0 || (frequency && frequency != frame.qpc_frequency)) conflict = true;
            frequency = frame.qpc_frequency;
            if (event.kind == NKS_EVENT_STARTED) { ++starts; start = event; start.image_name = {}; }
            else if (event.kind == NKS_EVENT_STOPPED) { ++stops; stop = event; stop.image_name = {}; }
            else conflict = true;
        }
    }
    bool paired() const { return starts && stops; }
    void print() const {
        if (!target.pid) return;
        std::cout << "Own fixture expected boot=" << std::hex << std::setfill('0') << std::setw(16) << target.boot_high
            << std::setw(16) << target.boot_low << std::dec << " pid=" << target.pid << " CreateTime=" << target.generation
            << " verifiedStarts=" << starts << " verifiedStops=" << stops << " rejectedOwnEvents=" << wrongIdentity
            << " QpcFrequency=" << frequency << " conflict=" << conflict << '\n';
    }
    void verify() const {
        require(starts == 1 && stops == 1 && wrongIdentity == 0 && !conflict,
            "The own child did not have exactly one verified Start and Stop; a missing endpoint is not evidence of liveness");
        require(start.timestamp_qpc <= stop.timestamp_qpc && frequency > 0, "Own child lifecycle QPC order or frequency was invalid");
        require(start.sequence != stop.sequence, "Own child endpoints reused the same source sequence");
        require((stop.flags & NKS_EVENT_HAS_EXIT_STATUS) != 0 && static_cast<std::uint32_t>(stop.exit_status) == childExit,
            "Own child Stop did not carry the independently observed exit code");
        require((start.flags & NKS_EVENT_HAS_PARENT_PID) == 0 || start.parent_pid == GetCurrentProcessId(),
            "Own child Start identified a different parent");
    }
};

struct Statistics {
    std::uint32_t state{NKS_EVENTS_IDLE}, error{}, flags{}, identityProvider{NKS_SAMPLE_UNAVAILABLE};
    std::uint64_t eventsLost{}, buffersLost{}, dropped{}, decodeErrors{}, ambiguous{}, budgetUsed{};
    bool reset{};
    void observe(const Frame& frame) {
        const auto& info = *frame.view;
        reset |= info.provider_events_lost < eventsLost || info.provider_buffers_lost < buffersLost
            || info.queue_dropped < dropped || info.decode_errors < decodeErrors;
        eventsLost = (std::max)(eventsLost, info.provider_events_lost);
        buffersLost = (std::max)(buffersLost, info.provider_buffers_lost);
        dropped = (std::max)(dropped, info.queue_dropped); decodeErrors = (std::max)(decodeErrors, info.decode_errors);
        ambiguous = (std::max)(ambiguous, info.ambiguous); budgetUsed = (std::max)(budgetUsed, frame.traffic.budget_bytes_used);
        state = info.state; error = info.win32_error; flags = info.flags; identityProvider = frame.traffic.identity_provider_state;
    }
    bool cleanupPending() const {
        return (flags & NKS_EVENTS_CLEANUP_OUTSTANDING) != 0
            || state == NKS_EVENTS_STARTING || state == NKS_EVENTS_RUNNING || state == NKS_EVENTS_STOPPING;
    }
    void verifyNoLoss() const {
        require(!eventsLost && !buffersLost && !dropped && !decodeErrors && !reset,
            "ETW reported loss, decode failures or a counter reset; this capture is incomplete");
    }
    void print() const {
        std::cout << "ETW state=" << state << " win32=" << error << " flags=" << flags << " identityProvider=" << identityProvider
            << " eventsLost=" << eventsLost << " buffersLost=" << buffersLost << " queueDropped=" << dropped
            << " decodeErrors=" << decodeErrors << " ambiguous=" << ambiguous << " counterReset=" << reset
            << " peakAccountedBytes=" << budgetUsed << '\n';
    }
};

class Session {
public:
    Session() { require(nks_events_create(&monitor) == NKS_OK && monitor, "Public event monitor creation failed"); }
    ~Session() { nks_events_stop(monitor); nks_events_release(monitor); }
    Session(const Session&) = delete;
    Session& operator=(const Session&) = delete;
    void start() { require(nks_events_start(monitor) == NKS_OK, "Public event monitor start failed"); }
    unsigned read() {
        Frame frame(monitor); statistics.observe(frame); capture.observe(*frame.view);
        return frame.view->event_count + frame.traffic.sample_count;
    }
    bool stopAndDrain() noexcept {
        nks_events_stop(monitor);
        const auto deadline = Clock::now() + 5s;
        try {
            do {
                const auto count = read();
                if (!statistics.cleanupPending() && count == 0) return true;
                std::this_thread::sleep_for(10ms);
            } while (Clock::now() < deadline);
        } catch (const std::exception& error) { std::cerr << "Cleanup read failed: " << error.what() << '\n'; }
        return false;
    }
    Capture capture;
    Statistics statistics;
    std::unique_ptr<Child> child;
private:
    NksEventMonitor* monitor{};
};

bool run(Session& session, bool requireCapture)
{
    session.start();
    const auto deadline = Clock::now() + 5s;
    for (;;) {
        session.read();
        const auto state = session.statistics.state;
        if (state == NKS_EVENTS_RUNNING) break;
        if (state == NKS_EVENTS_ACCESS_DENIED || state == NKS_EVENTS_UNSUPPORTED) {
            require(session.statistics.error != 0, "Denied or unsupported capture lacked its native error");
            require(!requireCapture, "--require-capture could not obtain a supported event capture");
            return false;
        }
        require(state == NKS_EVENTS_STARTING || state == NKS_EVENTS_IDLE, "Event monitor failed before Running");
        require(Clock::now() < deadline, "Event monitor did not reach a definitive state within five seconds");
        std::this_thread::sleep_for(25ms);
    }
    session.child = std::make_unique<Child>();
    session.capture.target = session.child->identity;
    session.child->resumeAndWait();
    const auto end = Clock::now() + 6s;
    do {
        session.read();
        if (session.capture.paired()) break;
        if (session.statistics.state != NKS_EVENTS_RUNNING) break;
        std::this_thread::sleep_for(25ms);
    } while (Clock::now() < end);
    return true;
}
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 2 && !wcscmp(argv[1], L"--child")) { Sleep(100); return static_cast<int>(childExit); }
    const bool requireCapture = argc == 2 && !wcscmp(argv[1], L"--require-capture");
    if (argc != 1 && !requireCapture) {
        std::cerr << "Usage: nks-process-event-live-tests [--require-capture]\n"; return 2;
    }
    try {
        std::cout << "Own token elevated=" << elevated() << " requireCapture=" << requireCapture << '\n';
        Session session;
        bool captured{}, failed{};
        try { captured = run(session, requireCapture); }
        catch (const std::exception& error) { failed = true; std::cerr << "FAIL ETW lifecycle: " << error.what() << '\n'; }
        if (failed) session.child.reset();
        const bool cleaned = session.stopAndDrain();
        session.statistics.print();
        session.capture.print();
        if (!cleaned) { std::cerr << "FAIL own ETW cleanup/drain was not confirmed within five seconds\n"; return 1; }
        if (failed) return 1;
        if (captured) {
            require(session.statistics.state == NKS_EVENTS_STOPPED, "The captured session ended with an error instead of a confirmed Stop");
            session.capture.verify(); session.statistics.verifyNoLoss();
            const auto& own = session.capture;
            std::cout << "PASS own lifetime boot=" << std::hex << std::setfill('0') << std::setw(16) << own.target.boot_high
                << std::setw(16) << own.target.boot_low << std::dec << " pid=" << own.target.pid
                << " CreateTime=" << own.target.generation << " StartQpc=" << own.start.timestamp_qpc
                << " StopQpc=" << own.stop.timestamp_qpc << " QpcFrequency=" << own.frequency
                << " exit=" << static_cast<std::uint32_t>(own.stop.exit_status) << " cleanup=confirmed\n";
        } else {
            require(session.statistics.state == NKS_EVENTS_ACCESS_DENIED || session.statistics.state == NKS_EVENTS_UNSUPPORTED,
                "A denied or unsupported result was replaced with an unexplained state");
            std::cout << "PASS explicit " << (session.statistics.state == NKS_EVENTS_ACCESS_DENIED ? "AccessDenied" : "Unsupported")
                << "; no capture or child lifetime claimed; cleanup=confirmed\n";
        }
        return 0;
    } catch (const std::exception& error) { std::cerr << "FAIL ETW lifecycle: " << error.what() << '\n'; return 1; }
}
