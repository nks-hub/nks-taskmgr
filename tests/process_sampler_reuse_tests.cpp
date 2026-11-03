#include "core/process_sampler.hpp"
#include <atomic>
#include <cmath>
#include <cstdlib>
#include <iostream>
#include <initializer_list>
#include <new>
#include <stdexcept>
#include <utility>

namespace {
std::atomic<bool> countAllocations{};
std::atomic<std::uint64_t> allocationCalls{};
void* allocate(std::size_t bytes)
{
    if (countAllocations.load(std::memory_order_relaxed)) allocationCalls.fetch_add(1, std::memory_order_relaxed);
    if (void* value = std::malloc(bytes ? bytes : 1)) return value;
    throw std::bad_alloc{};
}
}

void* operator new(std::size_t bytes) { return allocate(bytes); }
void* operator new[](std::size_t bytes) { return allocate(bytes); }
void operator delete(void* value) noexcept { std::free(value); }
void operator delete[](void* value) noexcept { std::free(value); }
void operator delete(void* value, std::size_t) noexcept { std::free(value); }
void operator delete[](void* value, std::size_t) noexcept { std::free(value); }

namespace {
using namespace nks;
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }

ProcessSnapshot snapshot(unsigned seconds, std::initializer_list<std::uint32_t> pids)
{
    ProcessSnapshot result; result.logicalProcessors = 4;
    result.boot = {{1, 2}, SampleState::valid};
    result.measuredAt = std::chrono::steady_clock::time_point(std::chrono::seconds(seconds));
    for (const auto pid : pids) {
        ProcessRecord row; row.identity = {{1, 2}, pid, GenerationKind::creationTime, 5000 + pid};
        row.created100ns = row.identity.generation; row.name = L"Process";
        row.counters.kernel100ns = 100000ull * pid + 1000000ull * seconds;
        row.counters.user100ns = 200000ull * pid + 2000000ull * seconds;
        row.counters.readIoBytes = 1000ull * pid + 10000ull * seconds;
        row.counters.writeIoBytes = 2000ull * pid + 20000ull * seconds;
        result.processes.push_back(std::move(row));
    }
    return result;
}
void measured(const ProcessRecord& row)
{
    require(row.cpuPercent.valid() && std::abs(row.cpuPercent.value - 7.5) < .000001
        && row.readIoBytesPerSecond.valid() && row.readIoBytesPerSecond.value == 10000
        && row.writeIoBytesPerSecond.valid() && row.writeIoBytesPerSecond.value == 20000,
        "A cached identity used another process's counters");
}
void first(const ProcessRecord& row)
{
    require(row.cpuPercent.state == SampleState::first && row.readIoBytesPerSecond.state == SampleState::first
        && row.writeIoBytesPerSecond.state == SampleState::first, "A new or missing lifetime inherited old rate samples");
}
void stableIdentitiesDoNotAllocate()
{
    ProcessSampler sampler; auto current = snapshot(1, {10, 20, 30}); sampler.apply(current);
    const auto before = allocationCalls.load(); countAllocations = true;
    for (unsigned i = 0; i < 128; ++i) {
        current.measuredAt += std::chrono::seconds(1);
        for (auto& row : current.processes) {
            row.counters.kernel100ns += 1000000; row.counters.user100ns += 2000000;
            row.counters.readIoBytes += 10000; row.counters.writeIoBytes += 20000;
        }
        sampler.apply(current);
    }
    countAllocations = false;
    require(allocationCalls.load() == before, "Stable process identities recreated heap allocations in the sampler");
    for (const auto& row : current.processes) measured(row);
}
void reorderAndMembershipChanges()
{
    ProcessSampler sampler; auto current = snapshot(1, {10, 20}); sampler.apply(current);
    current = snapshot(2, {20, 10}); sampler.apply(current);
    for (const auto& row : current.processes) measured(row);
    current = snapshot(3, {10, 30, 20}); sampler.apply(current);
    measured(current.processes[0]); first(current.processes[1]); measured(current.processes[2]);
    current = snapshot(4, {30, 20}); sampler.apply(current);
    for (const auto& row : current.processes) measured(row);
    current = snapshot(5, {10, 30, 20}); sampler.apply(current);
    first(current.processes[0]); measured(current.processes[1]); measured(current.processes[2]);
}
void fullLifetimeKeysRemainDistinct()
{
    ProcessSampler sampler; auto current = snapshot(1, {10, 20, 30, 40}); sampler.apply(current);
    current = snapshot(2, {40, 30, 20, 10});
    ++current.processes[0].identity.generation; ++current.processes[0].created100ns;
    ++current.processes[1].identity.boot.high;
    current.processes[2].identity.generationKind = GenerationKind::sequenceNumber;
    current.processes[3].identity.generationKind = GenerationKind::missing; current.processes[3].identity.generation = 0;
    sampler.apply(current);
    for (const auto& row : current.processes) first(row);
}
void resetAndEmptySnapshots()
{
    ProcessSampler sampler; auto current = snapshot(1, {10, 20}); sampler.apply(current); sampler.reset();
    current = snapshot(2, {20, 10}); sampler.apply(current);
    for (const auto& row : current.processes) first(row);
    current = snapshot(3, {10, 20}); sampler.apply(current);
    for (const auto& row : current.processes) measured(row);
    current = snapshot(4, {}); sampler.apply(current);
    current = snapshot(5, {10, 20}); sampler.apply(current);
    for (const auto& row : current.processes) first(row);
}
}

int main()
{
    try {
        stableIdentitiesDoNotAllocate(); reorderAndMembershipChanges(); fullLifetimeKeysRemainDistinct(); resetAndEmptySnapshots();
        std::cout << "4 process sampler reuse checks passed\n"; return 0;
    } catch (const std::exception& error) {
        countAllocations = false;
        std::cerr << "FAIL process sampler reuse: " << error.what() << '\n'; return 1;
    }
}
