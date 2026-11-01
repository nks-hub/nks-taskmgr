#include <phnt_windows.h>
#include <phnt.h>
#include "process_cpu_sets.hpp"
#include <bcrypt.h>
#include <algorithm>
#include <cstring>
#include <functional>

namespace nks::actions {
namespace {
struct ReadFailure { DWORD windows; LONG status{}; };
DWORD lastError() { const auto code = GetLastError(); return code ? code : ERROR_GEN_FAILURE; }
[[noreturn]] void fail(DWORD code) { throw ReadFailure{code}; }
bool ordered(std::span<const std::uint32_t> values)
{
    return values.size() <= maximumCpuSets && std::adjacent_find(values.begin(), values.end(), std::greater_equal<>()) == values.end();
}
bool guarded(const Context& context, HANDLE process, CpuSetsResult& output)
{
    Result checked; checked.target = output.result.target;
    applyHeldAction(context, Command::query, 0, 0, process, checked);
    const auto may = output.result.flags & static_cast<std::uint32_t>(mayHaveApplied);
    output.result = checked; output.result.flags |= may;
    if (checked.outcome == Outcome::inspected && (checked.flags & aliveAfter)) return true;
    output.snapshot = {};
    if (may && checked.outcome != Outcome::alreadyExited) output.result.outcome = Outcome::unverified;
    return false;
}
CpuSetsHash seal(const Target& target, const CpuSetsSnapshot& snapshot)
{
    const std::array<std::byte, 8> domain{std::byte{'N'}, std::byte{'K'}, std::byte{'S'}, std::byte{'C'}, std::byte{'P'}, std::byte{'U'}, std::byte{'1'}, std::byte{}};
    std::vector<std::byte> bytes(domain.size() + targetBytes + 8 + snapshot.cpus.size() * 28 + snapshot.defaults.size() * 4);
    std::copy(domain.begin(), domain.end(), bytes.begin()); std::size_t at = domain.size();
    write(bytes, at, target.key.boot.low, 8); write(bytes, at + 8, target.key.boot.high, 8);
    write(bytes, at + 16, target.key.pid, 4); write(bytes, at + 20, static_cast<unsigned>(target.key.generationKind), 4);
    write(bytes, at + 24, target.key.generation, 8); write(bytes, at + 32, target.created100ns, 8); at += targetBytes;
    write(bytes, at, snapshot.cpus.size(), 4); write(bytes, at + 4, snapshot.defaults.size(), 4); at += 8;
    for (const auto& cpu : snapshot.cpus) {
        write(bytes, at, cpu.id, 4); write(bytes, at + 4, cpu.group, 4); write(bytes, at + 8, cpu.logicalProcessor, 4);
        write(bytes, at + 12, cpu.core, 4); write(bytes, at + 16, cpu.numa, 4); write(bytes, at + 20, cpu.lastLevelCache, 4);
        write(bytes, at + 24, cpu.efficiency, 4); at += 28;
    }
    for (const auto id : snapshot.defaults) { write(bytes, at, id, 4); at += 4; }
    BCRYPT_ALG_HANDLE algorithm = nullptr; CpuSetsHash result{};
    auto status = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0);
    if (status < 0) throw ReadFailure{RtlNtStatusToDosError(status), status};
    status = BCryptHash(algorithm, nullptr, 0, reinterpret_cast<PUCHAR>(bytes.data()), static_cast<ULONG>(bytes.size()),
        reinterpret_cast<PUCHAR>(result.data()), static_cast<ULONG>(result.size()));
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (status < 0) throw ReadFailure{RtlNtStatusToDosError(status), status};
    if (std::all_of(result.begin(), result.end(), [](auto value) { return value == std::byte{}; })) fail(ERROR_INVALID_DATA);
    return result;
}
CpuSetsSnapshot capture(HANDLE process, const Target& target)
{
    ULONG required = 0;
    const BOOL sized = GetSystemCpuSetInformation(nullptr, 0, &required, process, 0);
    if (!sized && GetLastError() != ERROR_INSUFFICIENT_BUFFER) fail(lastError());
    if (!required) fail(ERROR_NOT_SUPPORTED);
    if (required > 1048576) fail(ERROR_MORE_DATA);
    std::vector<std::byte> bytes(required); ULONG used = 0;
    if (!GetSystemCpuSetInformation(reinterpret_cast<PSYSTEM_CPU_SET_INFORMATION>(bytes.data()), required, &used, process, 0)) fail(lastError());
    if (!used || used > bytes.size()) fail(ERROR_INVALID_DATA);
    CpuSetsSnapshot snapshot;
    for (std::size_t at = 0; at < used;) {
        if (used - at < sizeof(DWORD) * 2) fail(ERROR_INVALID_DATA);
        SYSTEM_CPU_SET_INFORMATION entry{};
        std::memcpy(&entry, bytes.data() + at, sizeof(DWORD) * 2);
        if (entry.Size < sizeof(DWORD) * 2 || entry.Size > used - at) fail(ERROR_INVALID_DATA);
        if (entry.Type == CpuSetInformation) {
            if (entry.Size < offsetof(SYSTEM_CPU_SET_INFORMATION, CpuSet.AllFlags) + sizeof(BYTE)
                || snapshot.cpus.size() == maximumCpuSets) fail(ERROR_MORE_DATA);
            std::memcpy(&entry, bytes.data() + at, std::min<std::size_t>(entry.Size, sizeof(entry)));
            if (entry.CpuSet.LogicalProcessorIndex >= 64) fail(ERROR_NOT_SUPPORTED);
            snapshot.cpus.push_back({entry.CpuSet.Id, entry.CpuSet.Group, entry.CpuSet.LogicalProcessorIndex,
                entry.CpuSet.CoreIndex, entry.CpuSet.NumaNodeIndex, entry.CpuSet.LastLevelCacheIndex,
                entry.CpuSet.EfficiencyClass, static_cast<std::uint32_t>(entry.CpuSet.AllFlags & 15)});
        }
        at += entry.Size;
    }
    if (snapshot.cpus.empty()) fail(ERROR_NOT_SUPPORTED);
    std::sort(snapshot.cpus.begin(), snapshot.cpus.end(), [](const auto& a, const auto& b) { return a.id < b.id; });
    for (std::size_t i = 1; i < snapshot.cpus.size(); ++i) if (snapshot.cpus[i - 1].id == snapshot.cpus[i].id) fail(ERROR_INVALID_DATA);
    ULONG count = 0;
    const BOOL defaultSized = GetProcessDefaultCpuSets(process, nullptr, 0, &count);
    if (!defaultSized && GetLastError() != ERROR_INSUFFICIENT_BUFFER) fail(lastError());
    if (count > maximumCpuSets) fail(ERROR_MORE_DATA);
    if (count) {
        std::vector<ULONG> defaults(count); ULONG actual = 0;
        if (!GetProcessDefaultCpuSets(process, defaults.data(), count, &actual)) fail(lastError());
        if (actual > count) fail(ERROR_INVALID_DATA);
        snapshot.defaults.assign(defaults.begin(), defaults.begin() + actual);
    }
    std::sort(snapshot.defaults.begin(), snapshot.defaults.end());
    if (!ordered(snapshot.defaults)) fail(ERROR_INVALID_DATA);
    for (const auto id : snapshot.defaults) {
        const auto cpu = std::lower_bound(snapshot.cpus.begin(), snapshot.cpus.end(), id, [](const auto& row, auto value) { return row.id < value; });
        if (cpu == snapshot.cpus.end() || cpu->id != id) fail(ERROR_INVALID_DATA);
    }
    snapshot.baseline = seal(target, snapshot); snapshot.valid = true; return snapshot;
}
void readFailure(CpuSetsResult& output, const ReadFailure& failure)
{
    auto& result = output.result;
    result.outcome = result.flags & mayHaveApplied ? Outcome::unverified
        : failure.windows == ERROR_NOT_SUPPORTED || failure.windows == ERROR_CALL_NOT_IMPLEMENTED || failure.windows == ERROR_PROC_NOT_FOUND
        ? Outcome::unsupported : failure.windows == ERROR_ACCESS_DENIED ? Outcome::refused : Outcome::error;
    result.reason = failure.windows == ERROR_ACCESS_DENIED ? Reason::accessDenied : Reason::nativeUnavailable;
    result.error = failure.windows; result.ntStatus = failure.status;
    result.flags &= ~static_cast<std::uint32_t>(effectConfirmed); output.snapshot = {};
}
}

void processHeldCpuSets(const Context& context, Command command, const CpuSetsHash& baseline,
    std::span<const std::uint32_t> selected, HANDLE process, CpuSetsResult& output)
{
    if (command != Command::queryCpuSets && command != Command::cpuSets) {
        output.result.reason = Reason::invalidArgument; output.result.error = ERROR_INVALID_PARAMETER; return;
    }
    try {
        if (!guarded(context, process, output)) return;
        output.snapshot = capture(process, output.result.target);
        if (!guarded(context, process, output)) return;
        if (command == Command::queryCpuSets) return;
        auto refuse = [&](Reason reason, DWORD error) { output.result.outcome = Outcome::refused; output.result.reason = reason; output.result.error = error; };
        if (!ordered(selected)) { refuse(Reason::invalidArgument, ERROR_INVALID_PARAMETER); return; }
        if (output.snapshot.baseline != baseline) { refuse(Reason::readbackMismatch, ERROR_RETRY); return; }
        for (const auto id : selected) {
            const auto cpu = std::lower_bound(output.snapshot.cpus.begin(), output.snapshot.cpus.end(), id, [](const auto& row, auto value) { return row.id < value; });
            if (cpu == output.snapshot.cpus.end() || cpu->id != id) { refuse(Reason::invalidArgument, ERROR_INVALID_PARAMETER); return; }
            if ((cpu->flags & 2) && !(cpu->flags & 4)) { refuse(Reason::accessDenied, ERROR_ACCESS_DENIED); return; }
        }
        const auto topology = output.snapshot.cpus;
        const std::vector<ULONG> requested(selected.begin(), selected.end());
        if (!guarded(context, process, output)) return;
        output.result.flags |= mayHaveApplied;
        const BOOL applied = SetProcessDefaultCpuSets(process, requested.empty() ? nullptr : requested.data(), static_cast<ULONG>(requested.size()));
        const DWORD error = applied ? ERROR_SUCCESS : lastError();
        output.snapshot = capture(process, output.result.target);
        if (!guarded(context, process, output)) return;
        const bool matches = std::equal(selected.begin(), selected.end(), output.snapshot.defaults.begin(), output.snapshot.defaults.end())
            && std::equal(topology.begin(), topology.end(), output.snapshot.cpus.begin(), output.snapshot.cpus.end(), [](const auto& a, const auto& b) {
                return a.id == b.id && a.group == b.group && a.logicalProcessor == b.logicalProcessor && a.core == b.core
                    && a.numa == b.numa && a.lastLevelCache == b.lastLevelCache && a.efficiency == b.efficiency;
            });
        output.result.outcome = applied && matches ? Outcome::applied : Outcome::unverified;
        if (applied && matches) output.result.flags |= effectConfirmed;
        else { output.result.reason = Reason::readbackMismatch; output.result.error = error ? error : ERROR_RETRY; }
    } catch (const ReadFailure& failure) {
        if (guarded(context, process, output)) readFailure(output, failure);
    }
}
void processCpuSets(const Context& context, Command command, const CpuSetsHash& baseline,
    std::span<const std::uint32_t> selected, CpuSetsResult& output)
{
    DWORD access = PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE;
    if (command == Command::cpuSets) access |= PROCESS_SET_LIMITED_INFORMATION;
    Handle process(OpenProcess(access, FALSE, output.result.target.key.pid));
    if (!process) {
        const auto error = lastError(); output.result.reason = error == ERROR_ACCESS_DENIED ? Reason::accessDenied : Reason::identityUnverifiable;
        output.result.error = error; return;
    }
    processHeldCpuSets(context, command, baseline, selected, process.get(), output);
}
}
