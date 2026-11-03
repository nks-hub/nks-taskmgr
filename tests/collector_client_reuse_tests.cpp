#include <windows.h>
#include "core/collector_client.hpp"
#include <algorithm>
#include <array>
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <string_view>
#include <thread>
#include <utility>

namespace {
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
struct EventState {
    std::array<HANDLE, 32> live{};
    unsigned created{}, closed{}, signalled{}, cancelled{}, drained{};
    std::stop_source* stopOnReset{};
    std::stop_source* stopOnWait{};
    bool waitHookCalled{};
    bool keepWorkerSuspended{}, workerHeld{};
    HANDLE cancelledPipe{};
    OVERLAPPED* cancelledOperation{};
} eventStats;

HANDLE WINAPI countedCreateEvent(LPSECURITY_ATTRIBUTES attributes, BOOL manual, BOOL initial, LPCWSTR name)
{
    const auto result = ::CreateEventW(attributes, manual, initial, name);
    if (result) {
        require(manual && !initial && !name, "Collector event is not unnamed manual-reset and initially clear");
        const auto slot = std::find(eventStats.live.begin(), eventStats.live.end(), nullptr);
        require(slot != eventStats.live.end(), "Collector event tracking limit exceeded");
        *slot = result; ++eventStats.created;
    }
    return result;
}
BOOL WINAPI countedCloseHandle(HANDLE handle)
{
    const auto result = ::CloseHandle(handle);
    const auto slot = std::find(eventStats.live.begin(), eventStats.live.end(), handle);
    if (result && slot != eventStats.live.end()) { *slot = nullptr; ++eventStats.closed; }
    return result;
}
BOOL WINAPI countedSetEvent(HANDLE handle)
{
    const auto result = ::SetEvent(handle);
    if (result && std::find(eventStats.live.begin(), eventStats.live.end(), handle) != eventStats.live.end()) ++eventStats.signalled;
    return result;
}
BOOL WINAPI controlledResetEvent(HANDLE handle)
{
    if (auto source = std::exchange(eventStats.stopOnReset, nullptr)) source->request_stop();
    return ::ResetEvent(handle);
}
DWORD WINAPI controlledWait(DWORD count, const HANDLE* handles, BOOL all, DWORD milliseconds)
{
    if (count == 3) {
        if (auto source = std::exchange(eventStats.stopOnWait, nullptr)) {
            eventStats.waitHookCalled = true; source->request_stop();
        }
    }
    return ::WaitForMultipleObjects(count, handles, all, milliseconds);
}
BOOL WINAPI countedCancelIo(HANDLE pipe, OVERLAPPED* operation)
{
    const auto result = ::CancelIoEx(pipe, operation);
    if (result) { ++eventStats.cancelled; eventStats.cancelledPipe = pipe; eventStats.cancelledOperation = operation; }
    return result;
}
BOOL WINAPI countedOverlappedResult(HANDLE pipe, OVERLAPPED* operation, DWORD* transferred, BOOL wait)
{
    const auto result = ::GetOverlappedResult(pipe, operation, transferred, wait);
    const auto error = GetLastError();
    if (wait && eventStats.cancelledPipe == pipe && eventStats.cancelledOperation == operation) {
        if (result || error == ERROR_OPERATION_ABORTED) ++eventStats.drained;
        eventStats.cancelledPipe = nullptr; eventStats.cancelledOperation = nullptr;
    }
    SetLastError(error);
    return result;
}
DWORD WINAPI controlledResumeThread(HANDLE thread)
{
    if (std::exchange(eventStats.keepWorkerSuspended, false)) {
        require(::SuspendThread(thread) == 1, "Own worker was not in its original CREATE_SUSPENDED state");
        const auto previous = ::ResumeThread(thread);
        require(previous == 2, "Own worker did not retain exactly one suspend count");
        eventStats.workerHeld = true; return previous;
    }
    return ::ResumeThread(thread);
}
}

// Instrument only this exact production translation unit; worker and OS behavior remain unchanged.
#define CreateEventW countedCreateEvent
#define CloseHandle countedCloseHandle
#define SetEvent countedSetEvent
#define ResetEvent controlledResetEvent
#define WaitForMultipleObjects controlledWait
#define CancelIoEx countedCancelIo
#define GetOverlappedResult countedOverlappedResult
#define ResumeThread controlledResumeThread
#include "../src/core/collector_client.cpp"
#undef CreateEventW
#undef CloseHandle
#undef SetEvent
#undef ResetEvent
#undef WaitForMultipleObjects
#undef CancelIoEx
#undef GetOverlappedResult
#undef ResumeThread

namespace {
using Clock = std::chrono::steady_clock;
using namespace std::chrono_literals;

void emptyEvents()
{
    require(std::all_of(eventStats.live.begin(), eventStats.live.end(), [](HANDLE handle) { return handle == nullptr; }),
        "A collector event remained open after client destruction");
    require(eventStats.created == eventStats.closed, "Collector event create/close counts differ");
}

void repeatedCollection()
{
    emptyEvents(); const auto before = eventStats.created;
    {
        nks::CollectorClient processes(nks::collector::Kind::processes), resources(nks::collector::Kind::resources);
        std::stop_source old;
        auto processTime = processes.processes(old.get_token()).measuredAt;
        auto resourceTime = resources.resources({}).measuredAt;
        require(processTime != Clock::time_point{} && resourceTime != Clock::time_point{}, "Initial real collection is not measured");
        require(eventStats.created - before == 6, "Two connections did not create exactly six events");
        const auto signals = eventStats.signalled; old.request_stop();
        require(eventStats.signalled == signals, "A completed request retained its old stop callback");
        for (unsigned i = 0; i < 12; ++i) {
            const auto process = processes.processes({}).measuredAt;
            const auto resource = resources.resources({}).measuredAt;
            require(process > processTime && resource > resourceTime, "Repeated real collection did not advance both clocks");
            processTime = process; resourceTime = resource;
            require(eventStats.created - before == 6, "Steady-state requests created additional collector events");
        }
    }
    emptyEvents(); std::cout << "realCollection=26;connections=2;eventCreates=6;oldStopCallbackReleased=true\n";
}

void alreadyCancelled()
{
    emptyEvents(); const auto before = eventStats.created;
    std::stop_source stop; stop.request_stop(); bool cancelled{};
    {
        nks::CollectorClient client(nks::collector::Kind::resources);
        try { (void)client.resources(stop.get_token()); }
        catch (const std::runtime_error& error) { cancelled = std::string_view(error.what()) == "Collector request cancelled"; }
    }
    require(cancelled && eventStats.created == before, "A pre-cancelled initial request created events or started collection");
    emptyEvents(); std::cout << "alreadyCancelled=true;eventCreates=0\n";
}

void recover(nks::CollectorClient& client)
{
    const auto deadline = Clock::now() + 3s;
    for (;;) {
        try { require(client.resources({}).measuredAt != Clock::time_point{}, "Recovery did not collect a real sample"); return; }
        catch (const std::runtime_error& error) {
            if (std::string_view(error.what()) != "Previous collector worker is still terminating") throw;
            require(Clock::now() < deadline, "Cancelled worker did not terminate before recovery");
            std::this_thread::sleep_for(10ms);
        }
    }
}

void cancellation(bool atReset)
{
    emptyEvents(); const auto before = eventStats.created, cancelledBefore = eventStats.cancelled, drainedBefore = eventStats.drained;
    {
        nks::CollectorClient client(nks::collector::Kind::resources); std::stop_source stop;
        if (atReset) eventStats.stopOnReset = &stop; else eventStats.stopOnWait = &stop;
        eventStats.waitHookCalled = eventStats.workerHeld = false; eventStats.keepWorkerSuspended = !atReset; bool cancelled{};
        try { (void)client.resources(stop.get_token()); }
        catch (const std::runtime_error& error) { cancelled = std::string_view(error.what()) == "Collector request cancelled"; }
        eventStats.stopOnReset = eventStats.stopOnWait = nullptr;
        require(cancelled && stop.stop_requested(), "The actual request did not observe cancellation");
        if (!atReset) require(eventStats.workerHeld && eventStats.waitHookCalled && eventStats.cancelled > cancelledBefore && eventStats.drained > drainedBefore,
            "Pending I/O cancellation and synchronous drain were not exercised");
        require(eventStats.created - before == 3, "Cancellation created events beyond its one connection");
        recover(client);
        require(eventStats.created - before == 6, "Recovery did not replace one retired connection");
    }
    emptyEvents();
    std::cout << (atReset ? "stopBeforeCallback=true" : "pendingCancellationAndDrain=true") << ";recovery=true;eventCreates=6\n";
}

void silentWorkerTimeout()
{
    emptyEvents(); const auto before = eventStats.created, cancelledBefore = eventStats.cancelled, drainedBefore = eventStats.drained;
    {
        nks::CollectorClient client(nks::collector::Kind::resources); eventStats.workerHeld = false;
        eventStats.keepWorkerSuspended = true; const auto started = Clock::now(); bool timedOut{};
        try { (void)client.resources({}); }
        catch (const std::runtime_error& error) { timedOut = std::string_view(error.what()) == "Collector operation timed out"; }
        const auto elapsed = Clock::now() - started;
        require(timedOut && eventStats.workerHeld && elapsed >= 4500ms && elapsed < 10s,
            "The silent owned worker did not reach the original five-second request deadline");
        require(eventStats.cancelled > cancelledBefore && eventStats.drained > drainedBefore,
            "Timed-out pending I/O was not cancelled and synchronously drained");
        require(eventStats.created - before == 3, "A timed-out request created events beyond one connection");
        recover(client); require(eventStats.created - before == 6, "Timeout recovery did not replace the retired connection");
    }
    emptyEvents(); std::cout << "silentWorkerTimeout=true;pendingCancelDrain=true;recovery=true;eventCreates=6\n";
}
}

int main()
{
    try {
        repeatedCollection(); alreadyCancelled(); cancellation(true); cancellation(false); silentWorkerTimeout();
        emptyEvents(); std::cout << "collectorClientEventReuse=passed\n"; return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
