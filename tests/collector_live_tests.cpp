#include <winsock2.h>
#include <ws2tcpip.h>

#include "collectors/process_collector.hpp"
#include "collectors/resource_collector.hpp"
#include "core/process_sampler.hpp"

#include <psapi.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <iostream>
#include <map>
#include <set>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

namespace {

using namespace nks;
using Clock = std::chrono::steady_clock;
constexpr std::int64_t mib = 1024 * 1024;

void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }

const ProcessRecord& self(const ProcessSnapshot& snapshot)
{
    const auto found = std::find_if(snapshot.processes.begin(), snapshot.processes.end(),
        [](const auto& row) { return row.identity.pid == GetCurrentProcessId(); });
    require(found != snapshot.processes.end(), "Collector did not report its own process");
    return *found;
}

std::int64_t difference(std::uint64_t after, std::uint64_t before)
{
    return static_cast<std::int64_t>(after) - static_cast<std::int64_t>(before);
}

std::uint64_t ownCpu100ns()
{
    FILETIME created{}, exited{}, kernel{}, user{};
    require(GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user) != FALSE, "GetProcessTimes failed");
    const auto value = [](FILETIME time) { return (static_cast<std::uint64_t>(time.dwHighDateTime) << 32) | time.dwLowDateTime; };
    return value(kernel) + value(user);
}

void processorGroupsAndRealInterval()
{
    ProcessCollector collector;
    ProcessSampler sampler;
    (void)collector.collect();
    auto before = collector.collect(); sampler.apply(before);
    const auto cpuBefore = ownCpu100ns(); const auto began = Clock::now();
    std::jthread([] { const auto end = Clock::now() + std::chrono::seconds(1); while (Clock::now() < end) { } }).join();
    const auto cpuAfter = ownCpu100ns(); const auto ended = Clock::now();
    auto after = collector.collect(); sampler.apply(after);

    const auto all = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
    const auto groups = GetActiveProcessorGroupCount();
    DWORD perGroup = 0;
    std::set<std::pair<std::uint16_t, std::uint16_t>> seen;
    for (WORD group = 0; group < groups; ++group) perGroup += GetActiveProcessorCount(group);
    for (const auto& processor : after.processors) {
        require(static_cast<DWORD>(processor.index) < GetActiveProcessorCount(processor.group), "Logical processor number is outside its group");
        seen.insert({processor.group, processor.index});
    }
    require(all > 0 && after.logicalProcessors == all && perGroup == all, "Logical processor count ignores processor groups");
    require(after.processors.size() == all && seen.size() == all, "Logical processor list does not cover every group once");
    require(after.measurements.processCpu.normalizationDivisor == all, "Process CPU divisor differs from all processor groups");

    const auto& row = self(after);
    const auto reference = std::chrono::duration<double>(ended - began).count();
    const auto interval = after.measurements.processCpu.intervalNs / 1e9;
    const auto expected = static_cast<double>(cpuAfter - cpuBefore) / 1e7 / reference / all * 100;
    require(after.measurements.processCpu.intervalState == MeasurementIntervalState::measured
        && interval >= reference && interval - reference < .1, "Process CPU interval differs from the measured monotonic interval");
    require(row.cpuPercent.valid() && std::abs(row.cpuPercent.value - expected) <= expected * .15 + .2,
        "Process CPU differs from GetProcessTimes over the real interval");
    std::printf("PASS processor groups=%u logical=%lu divisor=%u; interval %.3f s (reference %.3f s); own CPU %.3f %% (reference %.3f %%)\n",
        static_cast<unsigned>(groups), static_cast<unsigned long>(all), after.measurements.processCpu.normalizationDivisor,
        interval, reference, row.cpuPercent.value, expected);
}

struct Observation {
    std::uint64_t workingSet{}, privateBytes{};
    ProcessSnapshot snapshot;
};

Observation observe(ProcessCollector& collector)
{
    Observation result{0, 0, collector.collect()};
    PROCESS_MEMORY_COUNTERS_EX reference{};
    require(GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&reference), sizeof(reference)) != FALSE,
        "GetProcessMemoryInfo failed");
    const auto& row = self(result.snapshot);
    result.workingSet = row.counters.workingSetBytes; result.privateBytes = row.counters.privateBytes;
    require(std::abs(difference(result.workingSet, reference.WorkingSetSize)) < 8 * mib
        && std::abs(difference(result.privateBytes, reference.PrivateUsage)) < 8 * mib,
        "Working set or private bytes differ from GetProcessMemoryInfo");
    return result;
}

void knownPrivateAllocation()
{
    constexpr std::int64_t size = 64 * mib, tolerance = 8 * mib;
    const auto matches = [](std::int64_t value, std::int64_t target) { return value >= target - tolerance && value <= target + tolerance; };
    ProcessCollector collector;
    (void)collector.collect();
    const auto base = observe(collector);
    auto* pages = static_cast<std::byte*>(VirtualAlloc(nullptr, size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    require(pages != nullptr, "Cannot commit test memory");
    const auto committed = observe(collector);
    std::memset(pages, 0x5a, size);
    const auto touched = observe(collector);
    VirtualFree(pages, 0, MEM_RELEASE);
    const auto released = observe(collector);

    const auto committedPrivate = difference(committed.privateBytes, base.privateBytes);
    const auto committedWorkingSet = difference(committed.workingSet, base.workingSet);
    const auto touchedWorkingSet = difference(touched.workingSet, committed.workingSet);
    const auto touchedPrivate = difference(touched.privateBytes, committed.privateBytes);
    const auto releasedPrivate = difference(touched.privateBytes, released.privateBytes);
    const auto releasedWorkingSet = difference(touched.workingSet, released.workingSet);
    require(matches(committedPrivate, size), "Committed allocation did not raise private bytes by its size");
    require(committedWorkingSet < tolerance, "Untouched committed pages were counted as working set");
    require(matches(touchedWorkingSet, size) && std::abs(touchedPrivate) < tolerance,
        "Touching committed pages did not move only the working set");
    require(matches(releasedPrivate, size) && matches(releasedWorkingSet, size), "Released allocation was still reported");

    const auto& memory = touched.snapshot.memory;
    PERFORMANCE_INFORMATION system{};
    require(GetPerformanceInfo(&system, sizeof(system)) != FALSE, "GetPerformanceInfo failed");
    const auto page = static_cast<std::uint64_t>(system.PageSize);
    require(memory.state == SampleState::valid && memory.physicalBytes >= memory.availableBytes
        && memory.commitLimitBytes >= memory.committedBytes && memory.committedBytes >= touched.privateBytes
        && memory.physicalBytes >= touched.workingSet, "System memory totals are inconsistent with process memory");
    require(std::abs(difference(memory.physicalBytes, system.PhysicalTotal * page)) < 64 * mib
        && std::abs(difference(memory.committedBytes, system.CommitTotal * page)) < 512 * mib
        && std::abs(difference(memory.commitLimitBytes, system.CommitLimit * page)) < 64 * mib,
        "System physical memory or commit differs from GetPerformanceInfo");
    const auto& metadata = touched.snapshot.measurements;
    require(metadata.processMemory.unit == MeasurementUnit::bytes && metadata.processMemory.basis == MeasurementBasis::gauge
        && metadata.systemMemory.unit == MeasurementUnit::bytes && metadata.systemMemory.basis == MeasurementBasis::gauge,
        "Memory values are not described as byte gauges");
    std::printf("PASS 64 MiB allocation: commit private %+.1f MiB, working set %+.1f MiB; touch working set %+.1f MiB, private %+.1f MiB; "
        "release private -%.1f MiB, working set -%.1f MiB; system commit %.0f/%.0f MiB, physical %.0f MiB\n",
        committedPrivate / double(mib), committedWorkingSet / double(mib), touchedWorkingSet / double(mib), touchedPrivate / double(mib),
        releasedPrivate / double(mib), releasedWorkingSet / double(mib), memory.committedBytes / double(mib),
        memory.commitLimitBytes / double(mib), memory.physicalBytes / double(mib));
}

// Unbuffered, write-through I/O so both the process counters and the physical disk see every byte.
struct TempFile {
    std::wstring path;
    HANDLE handle{INVALID_HANDLE_VALUE};
    TempFile()
    {
        wchar_t directory[MAX_PATH]{}, name[MAX_PATH]{};
        require(GetTempPathW(MAX_PATH, directory) != 0 && GetTempFileNameW(directory, L"nks", 0, name) != 0, "Cannot create a temporary file name");
        path = name;
        handle = CreateFileW(name, GENERIC_READ | GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
            FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
        require(handle != INVALID_HANDLE_VALUE, "Cannot open the unbuffered test file");
    }
    ~TempFile() { if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle); }
    void transfer(std::byte* buffer, DWORD chunk, std::int64_t total, bool write)
    {
        LARGE_INTEGER start{}; require(SetFilePointerEx(handle, start, nullptr, FILE_BEGIN) != FALSE, "Cannot rewind the test file");
        for (std::int64_t done = 0; done < total; done += chunk) {
            DWORD moved = 0;
            require((write ? WriteFile(handle, buffer, chunk, &moved, nullptr) : ReadFile(handle, buffer, chunk, &moved, nullptr)) != FALSE
                && moved == chunk, "Unbuffered test I/O failed");
        }
    }
};

void knownProcessAndDiskIo()
{
    constexpr std::int64_t size = 64 * mib;
    constexpr DWORD chunk = 1 * mib;
    auto* buffer = static_cast<std::byte*>(VirtualAlloc(nullptr, chunk, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    require(buffer != nullptr, "Cannot allocate the aligned I/O buffer");
    std::memset(buffer, 0x3c, chunk);
    TempFile file;
    ProcessCollector processes; ProcessSampler sampler; ResourceCollector resources;
    (void)processes.collect(); (void)resources.collect();
    auto before = processes.collect(); sampler.apply(before);
    const auto disksBefore = resources.collect();
    file.transfer(buffer, chunk, size, true);
    auto written = processes.collect(); sampler.apply(written);
    const auto disksAfter = resources.collect();
    file.transfer(buffer, chunk, size, false);
    auto read = processes.collect(); sampler.apply(read);
    VirtualFree(buffer, 0, MEM_RELEASE);

    const auto bytes = [](const ProcessSnapshot& snapshot, const Metric<double>& rate) {
        return rate.value * (snapshot.measurements.processCpu.intervalNs / 1e9);
    };
    const auto& writer = self(written);
    const auto& reader = self(read);
    const double processWritten = bytes(written, writer.writeIoBytesPerSecond), processRead = bytes(read, reader.readIoBytesPerSecond);
    const auto matchesSize = [](double value, double target) { return value >= target * .98 && value <= target * 1.05 + 2 * mib; };
    require(writer.writeIoBytesPerSecond.valid() && matchesSize(processWritten, size), "Process write rate does not match the bytes written");
    require(reader.readIoBytesPerSecond.valid() && matchesSize(processRead, size), "Process read rate does not match the bytes read");

    const double seconds = std::chrono::duration<double>(disksAfter.measuredAt - disksBefore.measuredAt).count();
    double diskWritten = 0;
    for (const auto& device : disksAfter.devices)
        if (device.kind == ResourceKind::disk && device.tertiary.valid()) diskWritten = std::max(diskWritten, device.tertiary.value * seconds);
    require(disksAfter.diskCountersAvailable && diskWritten >= size * .95 && diskWritten <= size * 4 + 256 * mib,
        "No physical disk reported the unbuffered write");
    std::printf("PASS 64 MiB unbuffered I/O: process write %.1f MiB, read %.1f MiB; busiest disk write %.1f MiB over %.2f s\n",
        processWritten / mib, processRead / mib, diskWritten / mib, seconds);
}

// Sends UDP to the TEST-NET-1 documentation address, which leaves through the default route and is never answered.
bool knownNetworkSend()
{
    char enabled[8]{}; size_t length = 0;
    if (getenv_s(&length, enabled, sizeof(enabled), "NKS_LIVE_NETWORK") != 0 || std::string_view(enabled) != "1") {
        std::printf("SKIP network send: set NKS_LIVE_NETWORK=1 to send 16 MiB of UDP to 192.0.2.1\n");
        return false;
    }
    constexpr std::int64_t size = 16 * mib;
    constexpr int datagram = 1200;
    WSADATA data{};
    require(WSAStartup(MAKEWORD(2, 2), &data) == 0, "WSAStartup failed");
    const SOCKET socket = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    require(socket != INVALID_SOCKET, "Cannot open a UDP socket");
    sockaddr_in target{}; target.sin_family = AF_INET; target.sin_port = htons(9);
    require(inet_pton(AF_INET, "192.0.2.1", &target.sin_addr) == 1, "Cannot parse the documentation address");
    ResourceCollector resources;
    (void)resources.collect();
    const auto before = resources.collect();
    std::byte payload[datagram]{};
    for (std::int64_t sent = 0; sent < size; sent += datagram) {
        require(sendto(socket, reinterpret_cast<const char*>(payload), datagram, 0, reinterpret_cast<const sockaddr*>(&target), sizeof(target)) == datagram,
            "UDP send failed");
        if (sent % (256 * datagram) == 0) std::this_thread::sleep_for(std::chrono::milliseconds(15));
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(300));
    const auto after = resources.collect();
    closesocket(socket); WSACleanup();

    const double seconds = std::chrono::duration<double>(after.measuredAt - before.measuredAt).count();
    double best = 0, reported = 0;
    for (const auto& device : after.devices) {
        if (device.kind != ResourceKind::network || !device.octetsValid || !device.secondary.valid()) continue;
        const auto previous = std::find_if(before.devices.begin(), before.devices.end(), [&](const auto& row) { return row.id == device.id; });
        if (previous == before.devices.end() || !previous->octetsValid) continue;
        const double delta = static_cast<double>(device.sentOctets - previous->sentOctets);
        if (delta > best) { best = delta; reported = device.secondary.value / 8 * seconds; }
    }
    require(best >= size * .95 && best <= size * 1.5 + 64 * mib, "No network adapter counted the UDP payload");
    require(std::abs(reported - best) <= best * .05 + 64 * 1024, "Adapter send rate disagrees with its octet counter over the interval");
    std::printf("PASS 16 MiB UDP: busiest adapter sent %.1f MiB (rate x interval %.1f MiB) over %.2f s\n", best / mib, reported / mib, seconds);
    return true;
}

// An operator disables and re-enables the adapter that reaches the given IPv4 target while this check runs; totals since start
// must stay monotonic, keep their start time and grow by the known traffic only, not by the counter reset.
bool adapterReconnectTotals()
{
    char address[16]{}; size_t length = 0;
    if (getenv_s(&length, address, sizeof(address), "NKS_LIVE_ADAPTER_RESET") != 0 || length <= 1) {
        std::printf("SKIP adapter reconnect: set NKS_LIVE_ADAPTER_RESET=<IPv4, e.g. a subnet broadcast> and disable/enable its adapter within 90 s\n");
        return false;
    }
    constexpr std::int64_t burst = 4 * mib;
    constexpr int datagram = 1200;
    WSADATA data{};
    require(WSAStartup(MAKEWORD(2, 2), &data) == 0, "WSAStartup failed");
    const SOCKET socket = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    require(socket != INVALID_SOCKET, "Cannot open a UDP socket");
    sockaddr_in target{}; target.sin_family = AF_INET; target.sin_port = htons(9);
    require(inet_pton(AF_INET, address, &target.sin_addr) == 1, "Cannot parse NKS_LIVE_ADAPTER_RESET");
    const BOOL broadcast = TRUE;
    require(setsockopt(socket, SOL_SOCKET, SO_BROADCAST, reinterpret_cast<const char*>(&broadcast), sizeof(broadcast)) == 0, "Cannot allow broadcast");
    const auto send = [&] {
        std::byte payload[datagram]{};
        for (std::int64_t sent = 0; sent < burst; sent += datagram) {
            // The route can vanish while the adapter is disabled; a failed datagram is simply not counted.
            (void)sendto(socket, reinterpret_cast<const char*>(payload), datagram, 0, reinterpret_cast<const sockaddr*>(&target), sizeof(target));
            if (sent % (256 * datagram) == 0) std::this_thread::sleep_for(std::chrono::milliseconds(15));
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(300));
    };
    ResourceCollector resources;
    NetworkTransferTotals totals;
    std::map<std::wstring, NetworkTransfer> last;
    const auto sample = [&] {
        auto snapshot = resources.collect();
        totals.apply(snapshot, std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::system_clock::now().time_since_epoch()).count());
        for (const auto& device : snapshot.devices) {
            if (device.kind != ResourceKind::network) continue;
            if (const auto previous = last.find(device.id); previous != last.end())
                require(device.transfer.receivedBytes >= previous->second.receivedBytes && device.transfer.sentBytes >= previous->second.sentBytes
                    && device.transfer.startedAtUnixNs == previous->second.startedAtUnixNs, "Adapter totals decreased or restarted");
            last[device.id] = device.transfer;
        }
        return snapshot;
    };
    (void)sample();
    const auto initial = last;
    send();
    const auto first = sample();
    std::wstring id; std::uint64_t firstSent = 0;
    for (const auto& device : first.devices) {
        if (device.kind != ResourceKind::network || !initial.contains(device.id)) continue;
        const auto grown = device.transfer.sentBytes - initial.at(device.id).sentBytes;
        if (grown > firstSent) { firstSent = grown; id = device.id; }
    }
    require(firstSent >= burst * .95, "No adapter total counted the first UDP burst");
    const auto findTarget = [&](const ResourceSnapshot& snapshot) -> const ResourceRecord* {
        const auto found = std::find_if(snapshot.devices.begin(), snapshot.devices.end(), [&](const auto& row) { return row.id == id; });
        return found == snapshot.devices.end() ? nullptr : &*found;
    };
    std::uint64_t raw = findTarget(first)->sentOctets;
    std::printf("WAITING adapter reconnect: disable and re-enable the adapter now\n");
    std::fflush(stdout);
    bool outage = false; int stable = 0, samples = 0;
    for (const auto deadline = Clock::now() + std::chrono::seconds(90); Clock::now() < deadline && stable < 3; ++samples) {
        std::this_thread::sleep_for(std::chrono::milliseconds(500));
        const auto snapshot = sample();
        const auto* device = findTarget(snapshot);
        const bool valid = device != nullptr && device->octetsValid;
        if (!valid || device->sentOctets < raw) outage = true;
        if (valid) raw = device->sentOctets;
        stable = outage && valid && device->transfer.state == SampleState::valid ? stable + 1 : 0;
    }
    require(outage, "The adapter was not disabled and re-enabled within 90 s");
    require(stable >= 3, "The adapter did not come back with valid counters");
    // A re-enabled adapter reports counters before its address and route are back; retry until it transmits again.
    std::uint64_t secondSent = 0; int attempts = 0;
    for (const auto deadline = Clock::now() + std::chrono::seconds(30); Clock::now() < deadline && secondSent < burst * .95; ++attempts) {
        const auto beforeSecond = last.at(id).sentBytes;
        send();
        (void)sample();
        secondSent = last.at(id).sentBytes - beforeSecond;
        require(secondSent <= burst * 1.5 + 4 * mib, "The reconnected adapter counted more than the second burst");
        if (secondSent < burst * .95) std::this_thread::sleep_for(std::chrono::seconds(1));
    }
    closesocket(socket); WSACleanup();
    const auto whole = last.at(id).sentBytes - initial.at(id).sentBytes;
    require(secondSent >= burst * .95, "The reconnected adapter did not count the second burst within 30 s");
    require(whole <= (1 + attempts) * burst * 1.5 + 16 * mib, "The adapter total jumped across the reconnect");
    std::printf("PASS adapter reconnect: %d samples, outage seen; first burst %.1f MiB, second %.1f MiB after %d attempts, whole %.1f MiB, totals monotonic with unchanged start\n",
        samples, firstSent / double(mib), secondSent / double(mib), attempts, whole / double(mib));
    return true;
}

// Moves the system clock back one hour for a few seconds (administrator only) and requires process identity and CPU
// intervals to ignore the wall clock. The clock is restored by monotonic elapsed time even when a check fails.
bool clockShiftIdentity()
{
    char enabled[8]{}; size_t length = 0;
    if (getenv_s(&length, enabled, sizeof(enabled), "NKS_LIVE_CLOCK_SHIFT") != 0 || std::string_view(enabled) != "1") {
        std::printf("SKIP clock shift: set NKS_LIVE_CLOCK_SHIFT=1 as administrator to move the system clock back one hour for a few seconds\n");
        return false;
    }
    HANDLE token{};
    require(OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token) != FALSE, "Own token unavailable");
    TOKEN_PRIVILEGES privilege{1, {{{}, SE_PRIVILEGE_ENABLED}}};
    const bool adjusted = LookupPrivilegeValueW(nullptr, SE_SYSTEMTIME_NAME, &privilege.Privileges[0].Luid)
        && AdjustTokenPrivileges(token, FALSE, &privilege, 0, nullptr, nullptr) && GetLastError() == ERROR_SUCCESS;
    CloseHandle(token);
    require(adjusted, "The system time privilege is not available to this process");
    constexpr std::uint64_t hour = 3600ull * 10'000'000;
    const auto set = [](std::uint64_t value) {
        const FILETIME time{static_cast<DWORD>(value), static_cast<DWORD>(value >> 32)}; SYSTEMTIME system{};
        require(FileTimeToSystemTime(&time, &system) && SetSystemTime(&system), "SetSystemTime failed");
    };
    const auto now = [] { FILETIME time{}; GetSystemTimeAsFileTime(&time); return (static_cast<std::uint64_t>(time.dwHighDateTime) << 32) | time.dwLowDateTime; };
    const auto spin = [] { std::jthread([] { const auto end = Clock::now() + std::chrono::seconds(1); while (Clock::now() < end) { } }).join(); };

    ProcessCollector collector; ProcessSampler sampler;
    (void)collector.collect();
    auto before = collector.collect(); sampler.apply(before);
    const auto identity = self(before).identity;
    const auto original = now(); const auto shiftedAt = Clock::now();
    struct Restore {
        std::uint64_t original; Clock::time_point at; decltype(set)& apply; bool active{true};
        void operator()() { if (!active) return; active = false;
            apply(original + static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now() - at).count() / 100)); }
        ~Restore() { try { (*this)(); } catch (...) {} }
    } restore{original, shiftedAt, set};
    set(original - hour);
    const auto shifted = now();
    spin();
    const auto cpuBefore = ownCpu100ns(); const auto began = Clock::now();
    spin();
    const auto cpuAfter = ownCpu100ns(); const auto ended = Clock::now();
    auto during = collector.collect(); sampler.apply(during);
    restore();
    const auto restored = now();
    auto after = collector.collect(); sampler.apply(after);

    require(original - shifted > hour - 10'000'000 && original - shifted < hour + 10'000'000, "The system clock did not move back one hour");
    require(restored >= original && restored - original < 60ull * 10'000'000, "The system clock was not restored to the elapsed time");
    require(self(during).identity == identity && self(after).identity == identity, "Own process identity changed with the system clock");
    require(during.measuredAt > before.measuredAt && after.measuredAt > during.measuredAt, "Measurement time followed the wall clock backwards");
    const auto all = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
    const auto interval = std::chrono::duration<double>(during.measuredAt - before.measuredAt).count();
    const auto expected = static_cast<double>(cpuAfter - cpuBefore) / 1e7 / std::chrono::duration<double>(ended - began).count() / all * 100;
    const auto& row = self(during);
    require(interval > 1.9 && interval < 3, "The CPU interval across the clock change is not the monotonic interval");
    require(row.cpuPercent.valid() && row.cpuPercent.value > expected * .7 && row.cpuPercent.value < expected * 1.6 + .5,
        "Own process CPU across the clock change differs from the busy reference");
    require(self(after).cpuPercent.valid() && self(after).cpuPercent.value >= 0 && self(after).cpuPercent.value <= 100,
        "Own process CPU after restoring the clock is invalid");
    std::printf("PASS clock shift: wall clock -%.0f s and restored (+%.2f s elapsed); identity pid=%u generation kind %u unchanged; interval %.3f s; own CPU %.2f %% (busy reference %.2f %%)\n",
        (original - shifted) / 1e7, (restored - original) / 1e7, identity.pid, static_cast<unsigned>(identity.generationKind), interval, row.cpuPercent.value, expected);
    return true;
}

// Saturates one engine of the hardware adapter with the most dedicated memory and commits 512 MiB on it. The GPU's own
// timestamps and DXGI's per-process budget are the references; other applications may only add to the adapter totals.
bool knownGpuLoad()
{
    char enabled[8]{}; size_t length = 0;
    if (getenv_s(&length, enabled, sizeof(enabled), "NKS_LIVE_GPU") != 0 || std::string_view(enabled) != "1") {
        std::printf("SKIP GPU load: set NKS_LIVE_GPU=1 to load the hardware adapter with the most dedicated memory\n");
        return false;
    }
    using Microsoft::WRL::ComPtr;
    ComPtr<IDXGIFactory1> factory;
    require(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))), "DXGI factory unavailable");
    ComPtr<IDXGIAdapter1> adapter; DXGI_ADAPTER_DESC1 chosen{};
    for (UINT index = 0;; ++index) {
        ComPtr<IDXGIAdapter1> candidate;
        if (factory->EnumAdapters1(index, &candidate) != S_OK) break;
        DXGI_ADAPTER_DESC1 description{};
        if (FAILED(candidate->GetDesc1(&description)) || (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) continue;
        if (!adapter || description.DedicatedVideoMemory > chosen.DedicatedVideoMemory) { adapter = candidate; chosen = description; }
    }
    if (!adapter || chosen.DedicatedVideoMemory < static_cast<SIZE_T>(1024 * mib)) {
        std::printf("SKIP GPU load: no hardware adapter with at least 1 GiB of dedicated memory\n");
        return false;
    }
    ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; ComPtr<IDXGIAdapter3> budget;
    require(SUCCEEDED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context)),
        "D3D11 device unavailable on the chosen adapter");
    require(SUCCEEDED(adapter.As(&budget)), "DXGI video memory budget unavailable");
    wchar_t luid[48]{};
    swprintf_s(luid, L"luid_0x%08x_0x%08x", static_cast<unsigned>(chosen.AdapterLuid.HighPart), chosen.AdapterLuid.LowPart);
    const auto record = [&](const ResourceSnapshot& snapshot) {
        const auto found = std::find_if(snapshot.devices.begin(), snapshot.devices.end(),
            [&](const auto& row) { return row.kind == ResourceKind::gpu && row.id == luid; });
        require(found != snapshot.devices.end(), "Collector did not report the loaded adapter");
        return *found;
    };
    const auto ownLocal = [&] {
        DXGI_QUERY_VIDEO_MEMORY_INFO info{};
        require(SUCCEEDED(budget->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info)), "Own video memory usage unavailable");
        return static_cast<double>(info.CurrentUsage);
    };
    const auto query = [&](D3D11_QUERY type) {
        const D3D11_QUERY_DESC description{type, 0}; ComPtr<ID3D11Query> result;
        require(SUCCEEDED(device->CreateQuery(&description, &result)), "GPU query unavailable");
        return result;
    };
    const auto finish = [&] {
        const auto done = query(D3D11_QUERY_EVENT); context->End(done.Get()); BOOL value{};
        while (context->GetData(done.Get(), &value, sizeof(value), 0) != S_OK) std::this_thread::yield();
    };

    constexpr char source[] = "RWByteAddressBuffer o : register(u0);\n"
        "[numthreads(256, 1, 1)] void main(uint3 id : SV_DispatchThreadID) {\n"
        "  uint v = id.x; [loop] for (uint i = 0; i < 2048; ++i) v = v * 1664525u + 1013904223u;\n"
        "  o.Store((id.x & 0xFFFFFu) * 4, v); }\n";
    ComPtr<ID3DBlob> code, errors; ComPtr<ID3D11ComputeShader> shader;
    require(SUCCEEDED(D3DCompile(source, sizeof(source) - 1, "load", nullptr, nullptr, "main", "cs_5_0", 0, 0, &code, &errors)), "Load shader did not compile");
    require(SUCCEEDED(device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &shader)), "Load shader unavailable");

    ResourceCollector resources;
    (void)resources.collect();
    const auto idle = record(resources.collect());
    require(idle.secondary.valid(), "Dedicated GPU memory is unavailable before the load");
    const double ownBefore = ownLocal();
    constexpr UINT blockBytes = 128 * mib; constexpr int blocks = 4;
    std::vector<ComPtr<ID3D11Buffer>> buffers; std::vector<ComPtr<ID3D11UnorderedAccessView>> views;
    for (UINT block = 0; block < blocks; ++block) {
        const D3D11_BUFFER_DESC description{blockBytes, D3D11_USAGE_DEFAULT, D3D11_BIND_UNORDERED_ACCESS, 0, D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS, 0};
        ComPtr<ID3D11Buffer> buffer; ComPtr<ID3D11UnorderedAccessView> view;
        require(SUCCEEDED(device->CreateBuffer(&description, nullptr, &buffer)), "Known GPU buffer allocation failed");
        D3D11_UNORDERED_ACCESS_VIEW_DESC access{}; access.Format = DXGI_FORMAT_R32_TYPELESS; access.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
        access.Buffer.NumElements = blockBytes / 4; access.Buffer.Flags = D3D11_BUFFER_UAV_FLAG_RAW;
        require(SUCCEEDED(device->CreateUnorderedAccessView(buffer.Get(), &access, &view)), "Known GPU buffer view unavailable");
        const UINT fill[4]{block + 1, 0, 0, 0};
        context->ClearUnorderedAccessViewUint(view.Get(), fill);
        buffers.push_back(buffer); views.push_back(view);
    }
    finish();
    const double ownDelta = ownLocal() - ownBefore;

    context->CSSetShader(shader.Get(), nullptr, 0);
    ID3D11UnorderedAccessView* target = views.front().Get();
    context->CSSetUnorderedAccessViews(0, 1, &target, nullptr);
    struct Batch { ComPtr<ID3D11Query> disjoint, start, end; };
    const auto submit = [&](UINT groups) {
        Batch batch{query(D3D11_QUERY_TIMESTAMP_DISJOINT), query(D3D11_QUERY_TIMESTAMP), query(D3D11_QUERY_TIMESTAMP)};
        context->Begin(batch.disjoint.Get()); context->End(batch.start.Get());
        context->Dispatch(groups, 1, 1);
        context->End(batch.end.Get()); context->End(batch.disjoint.Get());
        return batch;
    };
    const auto seconds = [&](const Batch& batch) {
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT clock{}; UINT64 start{}, end{};
        while (context->GetData(batch.disjoint.Get(), &clock, sizeof(clock), 0) != S_OK) std::this_thread::yield();
        require(context->GetData(batch.start.Get(), &start, sizeof(start), 0) == S_OK && context->GetData(batch.end.Get(), &end, sizeof(end), 0) == S_OK
            && !clock.Disjoint && clock.Frequency && end >= start, "GPU timestamps are unavailable or disjoint");
        return double(end - start) / double(clock.Frequency);
    };
    UINT groups = 4096;
    const double probe = seconds(submit(groups));
    groups = static_cast<UINT>(std::clamp(groups * 0.008 / (std::max)(probe, 1e-6), 64.0, 65535.0));

    std::atomic<bool> stop{};
    double busy{}, wall{};
    std::thread load([&] {
        std::vector<Batch> flight;
        const auto started = Clock::now();
        while (!stop) {
            flight.push_back(submit(groups));
            if (flight.size() >= 4) { busy += seconds(flight.front()); flight.erase(flight.begin()); }
        }
        for (const auto& batch : flight) busy += seconds(batch);
        wall = std::chrono::duration<double>(Clock::now() - started).count();
    });
    std::this_thread::sleep_for(std::chrono::milliseconds(700));
    (void)resources.collect();
    std::this_thread::sleep_for(std::chrono::seconds(2));
    const auto loaded = record(resources.collect());
    std::this_thread::sleep_for(std::chrono::milliseconds(300));
    stop = true; load.join();

    const double ownBusy = 100 * busy / wall;
    require(ownBusy >= 85, "The load did not keep its GPU engine busy enough to be a known reference");
    require(loaded.primary.valid(), "Busiest GPU engine utilization is unavailable under load");
    std::wstring engine;
    for (const auto& item : loaded.engines) if (item.utilization.valid() && item.utilization.value == loaded.primary.value) engine = item.name;
    require(loaded.primary.value >= ownBusy - 15 && loaded.primary.value <= 100.5, "Busiest GPU engine utilization differs from the timed load");
    require(ownDelta >= blocks * blockBytes * .9, "DXGI did not account the known allocation to this process");
    require(loaded.secondary.valid(), "Dedicated GPU memory is unavailable under load");
    const double adapterDelta = loaded.secondary.value - idle.secondary.value;
    require(std::abs(adapterDelta - ownDelta) <= 96 * mib, "Adapter dedicated memory change differs from this process's known allocation");
    std::printf("PASS GPU load: %ls; own busy %.1f %% (%u groups/batch), busiest engine %.1f %% (%ls); dedicated memory own +%.1f MiB, adapter +%.1f MiB\n",
        chosen.Description, ownBusy, groups, loaded.primary.value, engine.c_str(), ownDelta / mib, adapterDelta / mib);
    return true;
}

}

int main()
{
    try {
        processorGroupsAndRealInterval();
        knownPrivateAllocation();
        knownProcessAndDiskIo();
        const int network = knownNetworkSend() + adapterReconnectTotals() + knownGpuLoad() + clockShiftIdentity();
        std::cout << 3 + network << '/' << 3 + network << " live collector checks passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL live collector: " << error.what() << '\n';
        return 1;
    }
}
