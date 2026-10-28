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
    void set(const std::function<void(SYSTEM_PROCESS_INFORMATION&)>& change) {
        SYSTEM_PROCESS_INFORMATION raw{};
        std::memcpy(&raw, bytes.data(), offsetof(SYSTEM_PROCESS_INFORMATION, Threads));
        change(raw);
        std::memcpy(bytes.data(), &raw, offsetof(SYSTEM_PROCESS_INFORMATION, Threads));
    }
    void named(const wchar_t* name) {
        const auto length = std::wcslen(name) * sizeof(wchar_t);
        auto* text = bytes.data() + offsetof(SYSTEM_PROCESS_INFORMATION, Threads);
        require(length <= bytes.size() - offsetof(SYSTEM_PROCESS_INFORMATION, Threads), "Test process name exceeds test entry");
        std::memcpy(text, name, length);
        set([&](auto& raw) { raw.ImageName.Buffer = reinterpret_cast<PWSTR>(text); raw.ImageName.Length = static_cast<USHORT>(length); raw.ImageName.MaximumLength = static_cast<USHORT>(length); });
    }
};

void rejected(const std::function<void(NativeEntry&)>& corrupt) {
    NativeEntry entry;
    corrupt(entry);
    try { (void)nks::parseProcesses(entry.bytes, 5); }
    catch (const std::runtime_error&) { return; }
    throw std::runtime_error("Malformed kernel buffer was accepted");
}

nks::ProcessSnapshot sample(std::int64_t seconds = 1) {
    nks::ProcessSnapshot value;
    value.measuredAt = std::chrono::steady_clock::time_point(std::chrono::seconds(seconds));
    value.logicalProcessors = 4;
    nks::ProcessRecord process;
    process.identity = {100, 42, nks::GenerationKind::creationTime, 500};
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
                raw.WorkingSetSize = 4096; raw.PrivatePageCount = 8192; });
            const auto records = nks::parseProcesses(entry.bytes, 99);
            require(records.size() == 1 && records[0].name == L"example.exe", "Name or entry count is wrong");
            require(records[0].identity == nks::ProcessIdentity{99, 42, nks::GenerationKind::creationTime, 123}, "Wrong process identity");
            require(records[0].counters.workingSetBytes == 4096 && records[0].counters.privateBytes == 8192, "Working set and private memory were confused");
        }},
        {"truncated fixed header", [] { try { (void)nks::parseProcesses({}, 0); } catch (const std::runtime_error&) { return; } throw std::runtime_error("Empty buffer accepted"); }},
        {"next offset inside header", [] { rejected([](auto& e) { e.set([](auto& r) { r.NextEntryOffset = 8; }); }); }},
        {"next offset outside buffer", [] { rejected([](auto& e) { e.set([](auto& r) { r.NextEntryOffset = 0xfffffff8; }); }); }},
        {"thread count exceeds entry", [] { rejected([](auto& e) { e.set([](auto& r) { r.NumberOfThreads = 0xffffffff; }); }); }},
        {"odd UTF-16 byte length", [] { rejected([](auto& e) { e.set([](auto& r) { r.ImageName.Length = 1; r.ImageName.MaximumLength = 2; }); }); }},
        {"name pointer outside allocation", [] { rejected([](auto& e) { e.set([](auto& r) { r.ImageName.Buffer = reinterpret_cast<PWSTR>(1); r.ImageName.Length = r.ImageName.MaximumLength = 4; }); }); }},
        {"negative CPU counter", [] { rejected([](auto& e) { e.set([](auto& r) { r.KernelTime.QuadPart = -1; }); }); }},
        {"missing creation time is explicit", [] { NativeEntry e; const auto rows = nks::parseProcesses(e.bytes, 1); require(rows[0].identity.generationKind == nks::GenerationKind::missing, "Missing identity was treated as known"); }},
        {"maximum even UNICODE_STRING length", [] { NativeEntry e; e.bytes.resize(offsetof(SYSTEM_PROCESS_INFORMATION, Threads) + 65534); const std::wstring name(32767, L'x'); e.named(name.c_str()); const auto rows = nks::parseProcesses(e.bytes, 1); require(rows[0].name == name, "Maximum-length name was truncated"); }},
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
        {"same generation from another boot is distinct", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); auto b = sample(2); b.processes[0].identity.bootTime++; s.apply(b); require(!b.processes[0].cpuPercent.valid(), "Another boot inherited state"); }},
        {"sequence number is not a creation timestamp", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); auto b = sample(2); b.processes[0].identity.generationKind = nks::GenerationKind::sequenceNumber; s.apply(b); require(!b.processes[0].cpuPercent.valid(), "Generation kinds were merged"); }},
        {"counter reset creates a gap", [] { nks::ProcessSampler s; auto a = sample(); a.processes[0].counters.user100ns = 20; s.apply(a); auto b = sample(2); s.apply(b); require(b.processes[0].cpuPercent.state == nks::SampleState::gap, "Counter reset was not marked"); }},
        {"suspend interval creates a gap", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); auto b = sample(100); s.apply(b); require(b.processes[0].cpuPercent.state == nks::SampleState::gap, "Suspend interval is not marked"); }},
        {"topology change requires a new baseline", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); auto b = sample(2); b.logicalProcessors = 8; s.apply(b); require(b.processes[0].cpuPercent.state == nks::SampleState::gap, "Topology change reused a baseline"); }},
        {"process I/O uses the actual interval", [] { nks::ProcessSampler s; auto a = sample(); s.apply(a); auto b = sample(3); b.processes[0].counters.readIoBytes = 8192; s.apply(b); require(b.processes[0].readIoBytesPerSecond.valid() && b.processes[0].readIoBytesPerSecond.value == 4096, "Wrong I/O rate"); }},
        {"processor groups are summed without partial results", [] { failSecondGroup = false; const std::array<nks::ProcessorGroup, 2> groups{{{0, 2}, {1, 3}}}; const auto result = nks::queryProcessorTimes(groups, fakeGroup); require(result.state == nks::SampleState::valid && result.kernel100ns == 150 && result.user100ns == 100 && result.idle100ns == 50, "Processor group counters were not combined"); }},
        {"failed processor group invalidates the complete sample", [] { failSecondGroup = true; const std::array<nks::ProcessorGroup, 2> groups{{{0, 2}, {1, 3}}}; const auto result = nks::queryProcessorTimes(groups, fakeGroup); require(result.state != nks::SampleState::valid && result.kernel100ns == 0, "Partial processor groups were reported as a whole machine"); }},
        {"system kernel time includes idle", [] { nks::ProcessSampler s; auto a = sample(); a.processorState = nks::SampleState::valid; a.systemKernel100ns = 100; a.systemUser100ns = 100; a.systemIdle100ns = 40; s.apply(a); auto b = a; b.measuredAt += std::chrono::seconds(1); b.systemKernel100ns += 100; b.systemUser100ns += 100; b.systemIdle100ns += 40; s.apply(b); require(b.systemCpuPercent.valid() && std::abs(b.systemCpuPercent.value - 80) < .001, "Idle was subtracted from the wrong system total"); }},
        {"unavailable system counters stay unavailable", [] { nks::ProcessSampler s; auto a = sample(); a.processorState = nks::SampleState::valid; s.apply(a); auto b = sample(2); s.apply(b); require(b.systemCpuPercent.state == nks::SampleState::unavailable, "Missing system counters became a valid zero"); }},
    };
    unsigned failures = 0;
    for (const auto& [name, run] : cases) {
        try { run(); std::cout << "PASS " << name << '\n'; }
        catch (const std::exception& error) { ++failures; std::cerr << "FAIL " << name << ": " << error.what() << '\n'; }
    }
    std::cout << std::size(cases) - failures << '/' << std::size(cases) << " passed\n";
    return failures ? 1 : 0;
}
