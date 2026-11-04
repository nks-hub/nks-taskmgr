#include <phnt_windows.h>
#include <phnt.h>
#include "collectors/process_parser.hpp"
#include "collectors/process_query.h"
#include "collectors/processor_query.hpp"
#include "core/process_sampler.hpp"

#include <array>
#include <cmath>
#include <cstring>
#include <functional>
#include <iostream>
#include <stdexcept>

namespace {

void require(bool value, const char* text) { if (!value) throw std::runtime_error(text); }

struct NativeEntry {
    std::vector<std::byte> bytes;
    NativeEntry() : bytes(offsetof(SYSTEM_PROCESS_INFORMATION, Threads) + 128) { }
    void set(const std::function<void(SYSTEM_PROCESS_INFORMATION&)>& change, std::size_t offset = 0) {
        SYSTEM_PROCESS_INFORMATION raw{};
        std::memcpy(&raw, bytes.data() + offset, offsetof(SYSTEM_PROCESS_INFORMATION, Threads));
        change(raw);
        std::memcpy(bytes.data() + offset, &raw, offsetof(SYSTEM_PROCESS_INFORMATION, Threads));
    }
    void named(const wchar_t* name, std::size_t textOffset = offsetof(SYSTEM_PROCESS_INFORMATION, Threads),
        std::size_t entryOffset = 0) {
        const auto length = std::wcslen(name) * sizeof(wchar_t);
        auto* text = bytes.data() + textOffset;
        require(length <= bytes.size() - textOffset, "Test process name exceeds test buffer");
        std::memcpy(text, name, length);
        set([&](auto& raw) { raw.ImageName.Buffer = reinterpret_cast<PWSTR>(text); raw.ImageName.Length = static_cast<USHORT>(length); raw.ImageName.MaximumLength = static_cast<USHORT>(length); }, entryOffset);
    }
};

void rejected(const std::function<void(NativeEntry&)>& corrupt) {
    NativeEntry entry;
    corrupt(entry);
    try { (void)nks::parseProcesses(entry.bytes, {5, 6}); }
    catch (const std::runtime_error&) { return; }
    throw std::runtime_error("Malformed kernel buffer was accepted");
}

nks::ProcessSnapshot sample(std::int64_t seconds = 1) {
    nks::ProcessSnapshot value;
    value.measuredAt = std::chrono::steady_clock::time_point(std::chrono::seconds(seconds));
    value.logicalProcessors = 4;
    nks::ProcessRecord process;
    process.identity = {{100, 200}, 42, nks::GenerationKind::creationTime, 500};
    value.processes.push_back(process);
    return value;
}

ULONG queryCalls{};
enum class QueryCase { success, grow, enormous, endless, invalidReturn } queryCase{};
LONG NTAPI fakeQuery(ULONG informationClass, void*, ULONG capacity, ULONG* used) {
    require(informationClass == SystemProcessInformation, "Wrong query information class");
    ++queryCalls;
    if (queryCase == QueryCase::enormous) { *used = NKS_PROCESS_BUFFER_LIMIT + 1; return STATUS_INFO_LENGTH_MISMATCH; }
    if (queryCase == QueryCase::endless || (queryCase == QueryCase::grow && queryCalls == 1)) {
        *used = capacity + 4096; return STATUS_INFO_LENGTH_MISMATCH;
    }
    *used = queryCase == QueryCase::invalidReturn ? capacity + 1 : 128;
    return STATUS_SUCCESS;
}

void queryTest(QueryCase mode, LONG expected, ULONG expectedCalls) {
    void* buffer = nullptr;
    ULONG capacity = 0, used = 0;
    queryCalls = 0; queryCase = mode;
    const auto result = NksQueryProcessBuffer(fakeQuery, &buffer, &capacity, &used);
    NksFreeProcessBuffer(buffer);
    require(result == expected, "Unexpected bounded-query result");
    require(queryCalls == expectedCalls, "Unexpected number of query retries");
    require(expected != STATUS_SUCCESS || used == 128, "Query did not expose the valid byte length");
}

bool failSecondGroup{};
LONG NTAPI fakeGroup(USHORT group, void* output, ULONG capacity, ULONG* used) {
    if (group == 1 && failSecondGroup) return STATUS_ACCESS_DENIED;
    auto* entry = static_cast<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION*>(output);
    const auto count = capacity / sizeof(*entry);
    for (std::size_t i = 0; i < count; ++i) {
        entry[i] = {};
        entry[i].KernelTime.QuadPart = 30;
        entry[i].UserTime.QuadPart = 20;
        entry[i].IdleTime.QuadPart = 10;
    }
    *used = capacity;
    return STATUS_SUCCESS;
}

}

int main() {
    const std::pair<const char*, std::function<void()>> cases[] = {
        {"valid process name, creation time and independent memory fields", [] {
            NativeEntry entry;
            entry.named(L"example.exe");
            entry.set([](auto& raw) { raw.UniqueProcessId = reinterpret_cast<HANDLE>(42); raw.CreateTime.QuadPart = 123;
                raw.WorkingSetSize = 4096; raw.PrivatePageCount = 8192; raw.SessionId = 3; });
            const auto records = nks::parseProcesses(entry.bytes, {99, 199});
            require(records.size() == 1 && records[0].name == L"example.exe", "Name or entry count is wrong");
            require(records[0].identity == nks::ProcessIdentity{{99, 199}, 42, nks::GenerationKind::creationTime, 123}, "Wrong process identity");
            require(records[0].counters.workingSetBytes == 4096 && records[0].counters.privateBytes == 8192, "Working set and private memory were confused");
            require(records[0].sessionId == 3, "Session identifier was lost");
        }},
        {"truncated fixed header", [] { try { (void)nks::parseProcesses({}, {}); } catch (const std::runtime_error&) { return; } throw std::runtime_error("Empty buffer accepted"); }},
        {"next offset inside header", [] { rejected([](auto& e) { e.set([](auto& r) { r.NextEntryOffset = 8; }); }); }},
        {"next offset outside buffer", [] { rejected([](auto& e) { e.set([](auto& r) { r.NextEntryOffset = 0xfffffff8; }); }); }},
        {"thread count exceeds entry", [] { rejected([](auto& e) { e.set([](auto& r) { r.NumberOfThreads = 0xffffffff; }); }); }},
        {"odd UTF-16 byte length", [] { rejected([](auto& e) { e.set([](auto& r) { r.ImageName.Length = 1; r.ImageName.MaximumLength = 2; }); }); }},
        {"name pointer outside allocation", [] { rejected([](auto& e) { e.set([](auto& r) { r.ImageName.Buffer = reinterpret_cast<PWSTR>(1); r.ImageName.Length = r.ImageName.MaximumLength = 4; }); }); }},
        {"name pointer inside fixed header", [] { rejected([](auto& e) { e.set([&](auto& r) { r.ImageName.Buffer = reinterpret_cast<PWSTR>(e.bytes.data() + offsetof(SYSTEM_PROCESS_INFORMATION, BasePriority)); r.ImageName.Length = r.ImageName.MaximumLength = 4; }); }); }},
        {"name pointer inside thread array", [] { rejected([](auto& e) { e.named(L"thread.exe"); e.set([](auto& r) { r.NumberOfThreads = 1; }); }); }},
        {"name begins before thread array ends", [] { rejected([](auto& e) { e.named(L"overlap.exe", offsetof(SYSTEM_PROCESS_INFORMATION, Threads) + sizeof(SYSTEM_THREAD_INFORMATION) - sizeof(wchar_t)); e.set([](auto& r) { r.NumberOfThreads = 1; }); }); }},
        {"name belongs to following process entry", [] { rejected([](auto& e) {
            const auto size = e.bytes.size();
            e.bytes.resize(size * 2);
            e.named(L"next.exe", size + offsetof(SYSTEM_PROCESS_INFORMATION, Threads));
            e.set([size](auto& r) { r.NextEntryOffset = static_cast<ULONG>(size); });
        }); }},
        {"name belongs to preceding process entry", [] { rejected([](auto& e) {
            const auto size = e.bytes.size();
            e.bytes.resize(size * 2);
            e.named(L"previous.exe");
            e.named(L"previous.exe", offsetof(SYSTEM_PROCESS_INFORMATION, Threads), size);
            e.set([size](auto& r) { r.NextEntryOffset = static_cast<ULONG>(size); });
        }); }},
        {"name extends into following process entry", [] { rejected([](auto& e) {
            const auto size = e.bytes.size();
            e.bytes.resize(size * 2);
            e.set([&](auto& r) { r.NextEntryOffset = static_cast<ULONG>(size); r.ImageName.Buffer = reinterpret_cast<PWSTR>(e.bytes.data() + size - sizeof(wchar_t)); r.ImageName.Length = r.ImageName.MaximumLength = 4; });
        }); }},
        {"name extends beyond final process entry", [] { rejected([](auto& e) {
            e.set([&](auto& r) { r.ImageName.Buffer = reinterpret_cast<PWSTR>(e.bytes.data() + e.bytes.size() - sizeof(wchar_t)); r.ImageName.Length = r.ImageName.MaximumLength = 4; });
        }); }},
        {"small entries cannot repeatedly copy a shared maximum-length name", [] { rejected([](auto& e) {
            constexpr auto fixed = offsetof(SYSTEM_PROCESS_INFORMATION, Threads);
            constexpr std::size_t count = 128;
            e.bytes.resize(count * fixed + 65534);
            const std::wstring name(32767, L'x');
            e.named(name.c_str(), count * fixed);
            for (std::size_t i = 0; i < count; ++i)
                e.set([&](auto& r) { r.NextEntryOffset = i + 1 < count ? static_cast<ULONG>(fixed) : 0;
                    r.ImageName.Buffer = reinterpret_cast<PWSTR>(e.bytes.data() + count * fixed);
                    r.ImageName.Length = r.ImageName.MaximumLength = 65534; }, i * fixed);
        }); }},
        {"name fits exactly after threads at final entry boundary", [] {
            NativeEntry e;
            const auto text = offsetof(SYSTEM_PROCESS_INFORMATION, Threads) + sizeof(SYSTEM_THREAD_INFORMATION);
            e.bytes.resize(text + 4 * sizeof(wchar_t));
            e.named(L"last", text);
            e.set([](auto& r) { r.NumberOfThreads = 1; });
            require(nks::parseProcesses(e.bytes, {}).front().name == L"last", "Exact-boundary process name was rejected");
        }},
        {"distinct process names fit exactly before next entry", [] {
            NativeEntry e;
            const auto size = e.bytes.size();
            e.bytes.resize(size * 2);
            e.named(L"first123", size - 8 * sizeof(wchar_t));
            e.named(L"second", size * 2 - 6 * sizeof(wchar_t), size);
            e.set([size](auto& r) { r.NextEntryOffset = static_cast<ULONG>(size); });
            const auto rows = nks::parseProcesses(e.bytes, {});
            require(rows.size() == 2 && rows[0].name == L"first123" && rows[1].name == L"second", "Valid adjacent process names changed");
        }},
        {"empty name does not require a buffer pointer", [] {
            NativeEntry e;
            e.set([](auto& r) { r.ImageName.Buffer = reinterpret_cast<PWSTR>(1); r.ImageName.MaximumLength = 65535; });
            require(nks::parseProcesses(e.bytes, {}).front().name == L"System Idle Process", "Empty process name lost its fallback");
        }},
        {"unused name capacity does not limit readable name bytes", [] {
            NativeEntry e;
            e.named(L"capacity.exe");
            e.set([](auto& r) { r.ImageName.MaximumLength = 65535; });
            require(nks::parseProcesses(e.bytes, {}).front().name == L"capacity.exe", "Unused name capacity changed parsing");
        }},
        {"negative CPU counter", [] { rejected([](auto& e) { e.set([](auto& r) { r.KernelTime.QuadPart = -1; }); }); }},
        {"missing creation time is explicit", [] { NativeEntry e; const auto rows = nks::parseProcesses(e.bytes, {1, 2}); require(rows[0].identity.generationKind == nks::GenerationKind::missing, "Missing identity was treated as known"); }},
        {"maximum even UNICODE_STRING length", [] { NativeEntry e; e.bytes.resize(offsetof(SYSTEM_PROCESS_INFORMATION, Threads) + 65534); const std::wstring name(32767, L'x'); e.named(name.c_str()); const auto rows = nks::parseProcesses(e.bytes, {1, 2}); require(rows[0].name == name, "Maximum-length name was truncated"); }},
        {"process chain requires final zero offset", [] { rejected([](auto& e) { const auto size = static_cast<ULONG>(e.bytes.size()); e.set([size](auto& raw) { raw.NextEntryOffset = size; }); }); }},
        {"query retains successful buffer", [] { queryTest(QueryCase::success, STATUS_SUCCESS, 1); }},
        {"query grows after mismatch", [] { queryTest(QueryCase::grow, STATUS_SUCCESS, 2); }},
        {"query rejects excessive allocation", [] { queryTest(QueryCase::enormous, STATUS_INSUFFICIENT_RESOURCES, 1); }},
        {"query limits changing process lists", [] { queryTest(QueryCase::endless, STATUS_RETRY, NKS_PROCESS_QUERY_ATTEMPTS); }},
        {"query rejects returned length outside allocation", [] { queryTest(QueryCase::invalidReturn, STATUS_DATA_ERROR, 1); }},
        {"first CPU sample is not a valid zero", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); require(!a.processes[0].cpuPercent.valid(), "First sample is valid"); }},
        {"CPU normalizes a loaded logical processor", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); auto b = sample(2); b.processes[0].counters.user100ns = 10000000; s.apply(b); require(b.processes[0].cpuPercent.valid() && std::abs(b.processes[0].cpuPercent.value - 25) < .001, "CPU is not normalized across four logical processors"); }},
        {"unchanged counters produce a valid zero", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); auto b = sample(2); s.apply(b); require(b.processes[0].cpuPercent.valid() && b.processes[0].cpuPercent.value == 0, "Measured idle was confused with missing data"); }},
        {"PID reuse does not inherit CPU", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); auto b = sample(2); b.processes[0].identity.generation++; b.processes[0].counters.user100ns = 10000000; s.apply(b); require(b.processes[0].cpuPercent.state == nks::SampleState::first, "New PID generation inherited CPU"); }},
        {"same generation from another boot is distinct", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); auto b = sample(2); b.processes[0].identity.boot.high++; s.apply(b); require(!b.processes[0].cpuPercent.valid(), "Another boot inherited state"); }},
        {"wall-clock boot timestamp is not process identity", [] {
            nks::ProcessSampler sampler;
            auto before = sample(); before.bootTime = 1000000; sampler.apply(before);
            auto after = sample(2); after.bootTime = 1; after.wallTime -= std::chrono::hours(24);
            after.processes[0].counters.user100ns = 10000000; sampler.apply(after);
            require(before.processes[0].identity == after.processes[0].identity && after.processes[0].cpuPercent.valid()
                && std::abs(after.processes[0].cpuPercent.value - 25) < .001, "Wall-clock correction changed process identity or CPU");
        }},
        {"sequence number is not a creation timestamp", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); auto b = sample(2); b.processes[0].identity.generationKind = nks::GenerationKind::sequenceNumber; s.apply(b); require(!b.processes[0].cpuPercent.valid(), "Generation kinds were merged"); }},
        {"counter reset creates a gap", [] { nks::ProcessSampler s; auto a = sample(); a.processes[0].counters.user100ns = 20; s.apply(a); auto b = sample(2); s.apply(b); require(b.processes[0].cpuPercent.state == nks::SampleState::gap, "Counter reset was not marked"); }},
        {"suspend interval creates a gap", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); auto b = sample(100); s.apply(b); require(b.processes[0].cpuPercent.state == nks::SampleState::gap, "Suspend interval is not marked"); }},
        {"topology change requires a new baseline", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); auto b = sample(2); b.logicalProcessors = 8; s.apply(b); require(b.processes[0].cpuPercent.state == nks::SampleState::gap, "Topology change reused a baseline"); }},
        {"process I/O uses the actual interval", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); auto b = sample(3); b.processes[0].counters.readIoBytes = 8192; s.apply(b); require(b.processes[0].readIoBytesPerSecond.valid() && b.processes[0].readIoBytesPerSecond.value == 4096, "Wrong I/O rate"); }},
        {"sampler baseline outlives caller storage and retains independent observation times", [] {
            nks::ProcessSampler sampler;
            nks::ProcessSnapshot after;
            {
                auto before = sample(); before.processes[0].name.assign(4096, L'x');
                before.cpuModel = L"example CPU"; before.memoryFacts = {{L"speed", L"6000"}};
                before.processes[0].counters = {1000000, 2000000, 100, 200};
                before.processorState = nks::SampleState::valid;
                before.systemKernel100ns = 1000; before.systemUser100ns = 500; before.systemIdle100ns = 300;
                before.processors = {{0, 0, 1000, 500, 300}};
                before.measurements.systemCpu.observedAtNs = 1010000000;
                before.measurements.logicalCpu.observedAtNs = 1020000000;
                before.measurements.systemMemory.observedAtNs = 1030000000;
                after = before; sampler.apply(before);
                before.processes[0].counters = {900000000, 900000000, 900000000, 900000000};
                before.processors[0].kernel100ns = 900000000;
                before.measurements = {};
            }
            after.measuredAt += std::chrono::milliseconds(250);
            after.processes[0].counters.kernel100ns += 500000; after.processes[0].counters.user100ns += 750000;
            after.processes[0].counters.readIoBytes += 1024; after.processes[0].counters.writeIoBytes += 2048;
            after.systemKernel100ns += 100; after.systemUser100ns += 100; after.systemIdle100ns += 40;
            after.processors[0].kernel100ns += 100; after.processors[0].user100ns += 100; after.processors[0].idle100ns += 40;
            after.measurements.systemCpu.observedAtNs = 1280000000;
            after.measurements.logicalCpu.observedAtNs = 1310000000;
            after.measurements.systemMemory.observedAtNs = 1370000000;
            sampler.apply(after);
            const auto& process = after.processes[0];
            require(process.cpuPercent.valid() && process.cpuPercent.value == 12.5
                && process.readIoBytesPerSecond.valid() && process.readIoBytesPerSecond.value == 4096
                && process.writeIoBytesPerSecond.valid() && process.writeIoBytesPerSecond.value == 8192,
                "Process deltas retained caller storage or lost counters");
            require(after.systemCpuPercent.valid() && after.systemCpuPercent.value == 80
                && after.processors[0].cpuPercent.valid() && after.processors[0].cpuPercent.value == 80,
                "System or logical CPU baseline retained caller storage");
            const auto& metadata = after.measurements;
            require(metadata.processCpu.intervalNs == 250000000 && metadata.processIo.intervalNs == 250000000
                && metadata.processCpu.cadenceNs == 250000000 && metadata.processMemory.cadenceNs == 250000000
                && metadata.processCpu.normalizationDivisor == 4 && metadata.systemCpu.cadenceNs == 270000000
                && metadata.logicalCpu.cadenceNs == 290000000 && metadata.systemMemory.cadenceNs == 340000000,
                "Independent observation times or actual process interval changed");
            require(after.processes[0].name.size() == 4096 && after.memoryFacts[0].value == L"6000",
                "Sampler consumed the current snapshot text");
        }},
        {"sampler reset discards counters and all observation baselines", [] {
            nks::ProcessSampler sampler;
            auto before = sample(); before.measurements.systemCpu.observedAtNs = 1000000000;
            before.measurements.logicalCpu.observedAtNs = 1000000000;
            before.measurements.systemMemory.observedAtNs = 1000000000;
            sampler.apply(before); sampler.reset();
            auto after = sample(2); after.processes[0].counters.user100ns = 10000000;
            after.measurements.systemCpu.observedAtNs = 2000000000;
            after.measurements.logicalCpu.observedAtNs = 2000000000;
            after.measurements.systemMemory.observedAtNs = 2000000000;
            sampler.apply(after);
            require(after.processes[0].cpuPercent.state == nks::SampleState::first
                && after.processes[0].readIoBytesPerSecond.state == nks::SampleState::first
                && after.systemCpuPercent.state == nks::SampleState::first
                && after.measurements.processCpu.cadenceNs == 0 && after.measurements.systemCpu.cadenceNs == 0
                && after.measurements.logicalCpu.cadenceNs == 0 && after.measurements.systemMemory.cadenceNs == 0,
                "Reset retained an old counter or observation baseline");
            auto next = sample(3); next.processes[0].counters.user100ns = 20000000; sampler.apply(next);
            require(next.processes[0].cpuPercent.valid() && next.processes[0].cpuPercent.value == 25,
                "Reset did not retain the replacement baseline");
        }},
        {"topology gap replaces process and observation baselines", [] {
            nks::ProcessSampler sampler;
            auto before = sample(); before.measurements.systemCpu.observedAtNs = 1000000000; sampler.apply(before);
            auto changed = sample(2); changed.logicalProcessors = 8; changed.processes[0].counters.user100ns = 10000000;
            changed.measurements.systemCpu.observedAtNs = 2100000000; sampler.apply(changed);
            auto after = sample(3); after.logicalProcessors = 8; after.processes[0].counters.user100ns = 20000000;
            after.measurements.systemCpu.observedAtNs = 3300000000; sampler.apply(after);
            require(changed.processes[0].cpuPercent.state == nks::SampleState::gap
                && after.processes[0].cpuPercent.valid() && after.processes[0].cpuPercent.value == 12.5
                && after.measurements.processCpu.normalizationDivisor == 8 && after.measurements.systemCpu.cadenceNs == 1200000000,
                "Topology gap retained the previous valid sample as baseline");
        }},
        {"processor groups are summed without partial results", [] { failSecondGroup = false; const std::array<nks::ProcessorGroup, 2> groups{{{0, 2}, {1, 3}}}; const auto result = nks::queryProcessorTimes(groups, fakeGroup); require(result.state == nks::SampleState::valid && result.kernel100ns == 150 && result.user100ns == 100 && result.idle100ns == 50, "Processor group counters were not combined"); }},
        {"failed processor group invalidates the complete sample", [] { failSecondGroup = true; const std::array<nks::ProcessorGroup, 2> groups{{{0, 2}, {1, 3}}}; const auto result = nks::queryProcessorTimes(groups, fakeGroup); require(result.state != nks::SampleState::valid && result.kernel100ns == 0, "Partial processor groups were reported as a whole machine"); }},
        {"system kernel time includes idle", [] { nks::ProcessSampler s; auto a = sample(); a.processorState = nks::SampleState::valid; a.systemKernel100ns = 100; a.systemUser100ns = 100; a.systemIdle100ns = 40; s.apply(a); auto b = a; b.measuredAt += std::chrono::seconds(1); b.systemKernel100ns += 100; b.systemUser100ns += 100; b.systemIdle100ns += 40; s.apply(b); require(b.systemCpuPercent.valid() && std::abs(b.systemCpuPercent.value - 80) < .001, "Idle was subtracted from the wrong system total"); }},
        {"stationary logical core does not break valid total CPU", [] {
            nks::ProcessSampler sampler;
            auto before = sample(); before.processorState = nks::SampleState::valid;
            before.systemKernel100ns = 100; before.systemUser100ns = 100; before.systemIdle100ns = 40;
            before.processors = {{0, 0, 100, 100, 40}, {0, 1, 50, 50, 10}};
            sampler.apply(before);
            auto after = before; after.measuredAt += std::chrono::seconds(1);
            after.systemKernel100ns += 100; after.systemUser100ns += 100; after.systemIdle100ns += 40;
            after.processors[0].kernel100ns += 100; after.processors[0].user100ns += 100; after.processors[0].idle100ns += 40;
            sampler.apply(after);
            require(after.processors[1].cpuPercent.state == nks::SampleState::gap, "Stationary core invented a valid ratio");
            require(after.systemCpuPercent.valid() && std::abs(after.systemCpuPercent.value - 80) < .001,
                "A core without counter progress invalidated measured total CPU");
        }},
        {"logical counter reset still breaks aggregate CPU", [] {
            nks::ProcessSampler sampler;
            auto before = sample(); before.processorState = nks::SampleState::valid;
            before.systemKernel100ns = 100; before.systemUser100ns = 100; before.systemIdle100ns = 40;
            before.processors = {{0, 0, 100, 100, 40}}; sampler.apply(before);
            auto after = before; after.measuredAt += std::chrono::seconds(1);
            after.systemKernel100ns += 100; after.systemUser100ns += 100; after.systemIdle100ns += 40;
            after.processors[0].kernel100ns = 0; sampler.apply(after);
            require(after.systemCpuPercent.state == nks::SampleState::gap, "Logical counter rollback became a valid total");
        }},
        {"logical idle beyond elapsed still breaks aggregate CPU", [] {
            nks::ProcessSampler sampler;
            auto before = sample(); before.processorState = nks::SampleState::valid;
            before.systemKernel100ns = 100; before.systemUser100ns = 100; before.systemIdle100ns = 40;
            before.processors = {{0, 0, 100, 100, 40}}; sampler.apply(before);
            auto after = before; after.measuredAt += std::chrono::seconds(1);
            after.systemKernel100ns += 100; after.systemUser100ns += 100; after.systemIdle100ns += 40;
            after.processors[0].idle100ns += 1; sampler.apply(after);
            require(after.systemCpuPercent.state == nks::SampleState::gap, "Invalid logical idle became a valid total");
        }},
        {"first sample marks every rate as first with no measured interval", [] {
            nks::ProcessSampler sampler;
            auto first = sample(); first.processes[0].counters = {5000000, 5000000, 4096, 4096};
            first.processorState = nks::SampleState::valid; first.systemKernel100ns = 100; first.systemUser100ns = 100;
            first.processors = {{0, 0, 100, 100, 40}};
            sampler.apply(first);
            const auto& row = first.processes[0];
            require(row.cpuPercent.state == nks::SampleState::first && row.cpuPercent.value == 0
                && row.readIoBytesPerSecond.state == nks::SampleState::first && row.writeIoBytesPerSecond.state == nks::SampleState::first
                && first.systemCpuPercent.state == nks::SampleState::first && first.processors[0].cpuPercent.state == nks::SampleState::first,
                "First sample reported a measured rate");
            require(first.measurements.processCpu.intervalState == nks::MeasurementIntervalState::unavailable
                && first.measurements.processCpu.intervalNs == 0, "First sample claimed a measured interval");
            auto next = sample(2); next.processes.push_back(next.processes[0]); next.processes[1].identity.pid = 43;
            next.processes[0].counters = first.processes[0].counters; sampler.apply(next);
            require(next.processes[0].cpuPercent.valid() && next.processes[1].cpuPercent.state == nks::SampleState::first,
                "A process seen for the first time inherited another baseline");
        }},
        {"process and system CPU use distinct methods", [] {
            nks::ProcessSampler sampler; auto a = sample(); sampler.apply(a); auto b = sample(2); sampler.apply(b);
            const auto& m = b.measurements;
            require(m.processCpu.basis == nks::MeasurementBasis::elapsedDelta && m.processCpu.normalization == nks::MeasurementNormalization::wholeMachine
                && m.systemCpu.basis == nks::MeasurementBasis::counterRatio && m.systemCpu.normalization == nks::MeasurementNormalization::wholeMachine
                && m.logicalCpu.basis == nks::MeasurementBasis::counterRatio && m.logicalCpu.normalization == nks::MeasurementNormalization::logicalProcessor
                && m.processIo.basis == nks::MeasurementBasis::elapsedDelta && m.processIo.unit == nks::MeasurementUnit::bytesPerSecond,
                "CPU methods are not distinguishable in metadata");
        }},
        {"any decreasing process counter yields a gap without negative or huge rates", [] {
            for (int field = 0; field < 4; ++field) {
                nks::ProcessSampler sampler; auto a = sample(); a.processes[0].counters = {50000000, 50000000, 8192, 8192}; sampler.apply(a);
                auto b = sample(2); b.processes[0].counters = {60000000, 60000000, 16384, 16384};
                auto& counters = b.processes[0].counters;
                (field == 0 ? counters.kernel100ns : field == 1 ? counters.user100ns : field == 2 ? counters.readIoBytes : counters.writeIoBytes) = 1;
                sampler.apply(b);
                const auto& row = b.processes[0];
                for (const auto* metric : {&row.cpuPercent, &row.readIoBytesPerSecond, &row.writeIoBytesPerSecond})
                    require(metric->value >= 0 && metric->value <= 100000, "Counter rollback produced a negative or wrapped rate");
                require((field < 2) == (row.cpuPercent.state == nks::SampleState::gap)
                    && (field == 2) == (row.readIoBytesPerSecond.state == nks::SampleState::gap)
                    && (field == 3) == (row.writeIoBytesPerSecond.state == nks::SampleState::gap),
                    "Counter rollback was not isolated to its own rate");
                auto c = b; c.measuredAt += std::chrono::seconds(1);
                c.processes[0].counters.kernel100ns += 5000000; c.processes[0].counters.user100ns += 5000000;
                sampler.apply(c);
                require(c.processes[0].cpuPercent.valid() && std::abs(c.processes[0].cpuPercent.value - 25) < .001,
                    "Rolled-back counters did not become the next baseline");
            }
        }},
        {"CPU divides by the actual monotonic interval", [] {
            nks::ProcessSampler sampler; auto a = sample(); sampler.apply(a);
            auto b = sample(); b.measuredAt += std::chrono::milliseconds(7500); b.processes[0].counters.user100ns = 150000000;
            sampler.apply(b);
            require(b.processes[0].cpuPercent.valid() && std::abs(b.processes[0].cpuPercent.value - 50) < .001
                && b.measurements.processCpu.intervalNs == 7500000000 && b.measurements.processCpu.intervalState == nks::MeasurementIntervalState::measured,
                "CPU used a nominal instead of the measured interval");
        }},
        {"suspend boundary, non-advancing clock and recovery after a gap", [] {
            nks::ProcessSampler sampler; auto a = sample(); sampler.apply(a);
            auto b = sample(32); b.processes[0].counters.user100ns = 10000000; sampler.apply(b);
            require(b.processes[0].cpuPercent.state == nks::SampleState::gap && b.measurements.processCpu.intervalNs == 0
                && b.measurements.processCpu.intervalState == nks::MeasurementIntervalState::unavailable,
                "Interval over 30 s after suspend was measured");
            auto c = sample(33); c.processes[0].counters.user100ns = 20000000; sampler.apply(c);
            require(c.processes[0].cpuPercent.valid() && std::abs(c.processes[0].cpuPercent.value - 25) < .001,
                "Sample after suspend did not use the post-suspend baseline");
            auto same = c; sampler.apply(same);
            require(same.processes[0].cpuPercent.state == nks::SampleState::gap, "Zero interval produced a rate");
            nks::ProcessSampler edge; auto d = sample(); edge.apply(d);
            auto e = sample(31); e.processes[0].counters.user100ns = 120000000; edge.apply(e);
            require(e.processes[0].cpuPercent.valid() && std::abs(e.processes[0].cpuPercent.value - 10) < .001,
                "Exactly 30 s interval was rejected");
        }},
        {"processor groups use all logical processors as divisor", [] {
            nks::ProcessSampler sampler; auto a = sample(); a.logicalProcessors = 72;
            a.processorState = nks::SampleState::valid;
            for (std::uint16_t i = 0; i < 72; ++i)
                a.processors.push_back({static_cast<std::uint16_t>(i < 64 ? 0 : 1), static_cast<std::uint16_t>(i < 64 ? i : i - 64), 1000, 0, 1000});
            a.systemKernel100ns = 72000; a.systemIdle100ns = 72000;
            sampler.apply(a);
            auto b = a; b.measuredAt += std::chrono::seconds(1);
            b.processes[0].counters.user100ns = 360000000;
            for (auto& processor : b.processors) {
                processor.kernel100ns += 10000000;
                if (processor.group == 1) processor.idle100ns += 10000000;
            }
            b.systemKernel100ns += 720000000; b.systemIdle100ns += 80000000;
            sampler.apply(b);
            require(b.measurements.processCpu.normalizationDivisor == 72, "Divisor ignored the second processor group");
            require(b.processes[0].cpuPercent.valid() && std::abs(b.processes[0].cpuPercent.value - 50) < .001,
                "Process CPU was normalized by one processor group");
            require(b.systemCpuPercent.valid() && std::abs(b.systemCpuPercent.value - 64. / 72 * 100) < .001
                && b.processors[63].cpuPercent.value == 100 && b.processors[64].cpuPercent.valid() && b.processors[64].cpuPercent.value == 0,
                "System CPU omitted a processor group");
            auto c = b; c.measuredAt += std::chrono::seconds(1);
            c.processors[64].group = 0; sampler.apply(c);
            require(c.processors[64].cpuPercent.state == nks::SampleState::gap, "Logical processors from different groups were mixed");
            failSecondGroup = false;
            const std::array<nks::ProcessorGroup, 2> groups{{{0, 64}, {1, 8}}};
            const auto times = nks::queryProcessorTimes(groups, fakeGroup);
            require(times.state == nks::SampleState::valid && times.processors.size() == 72 && times.processors[64].group == 1
                && times.processors[64].index == 0 && times.processors[71].index == 7, "Group-relative processor numbers were lost");
        }},
        {"unavailable system counters stay unavailable",[] { nks::ProcessSampler s; auto a = sample(); a.processorState = nks::SampleState::valid; s.apply(a); auto b = sample(2); s.apply(b); require(b.systemCpuPercent.state == nks::SampleState::unavailable, "Missing system counters became a valid zero"); }},
    };
    unsigned failures = 0;
    for (const auto& [name, run] : cases) {
        try { run(); std::cout << "PASS " << name << '\n'; }
        catch (const std::exception& error) { ++failures; std::cerr << "FAIL " << name << ": " << error.what() << '\n'; }
    }
    std::cout << std::size(cases) - failures << '/' << std::size(cases) << " passed\n";
    return failures ? 1 : 0;
}
