#include <winsock2.h>
#include <ws2tcpip.h>

#include "collectors/process_collector.hpp"
#include "collectors/resource_collector.hpp"
#include "core/process_sampler.hpp"

#include <psapi.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <iostream>
#include <set>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
#include <utility>

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

}

int main()
{
    try {
        processorGroupsAndRealInterval();
        knownPrivateAllocation();
        knownProcessAndDiskIo();
        const bool network = knownNetworkSend();
        std::cout << (network ? "4/4" : "3/3") << " live collector checks passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL live collector: " << error.what() << '\n';
        return 1;
    }
}
