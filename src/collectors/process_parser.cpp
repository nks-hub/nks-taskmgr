#include <phnt_windows.h>
#include <phnt.h>
#include "process_parser.hpp"

#include <algorithm>
#include <cstring>
#include <limits>
#include <stdexcept>

namespace nks {

std::vector<ProcessRecord> parseProcesses(std::span<const std::byte> buffer, BootIdentifier boot)
{
    constexpr auto fixedSize = offsetof(SYSTEM_PROCESS_INFORMATION, Threads);
    if (buffer.size() < fixedSize)
        throw std::runtime_error("Truncated process information header");

    std::vector<ProcessRecord> records;
    records.reserve(buffer.size() / 2048);
    std::size_t offset = 0;
    while (offset < buffer.size()) {
        if (buffer.size() - offset < fixedSize)
            throw std::runtime_error("Truncated process information entry");
        SYSTEM_PROCESS_INFORMATION raw{};
        std::memcpy(&raw, buffer.data() + offset, fixedSize);
        const auto remaining = buffer.size() - offset;
        const auto entrySize = raw.NextEntryOffset ? raw.NextEntryOffset : remaining;
        if (entrySize < fixedSize || entrySize > remaining || (raw.NextEntryOffset % alignof(void*)) != 0)
            throw std::runtime_error("Invalid process entry offset");
        if (raw.NumberOfThreads > (entrySize - fixedSize) / sizeof(SYSTEM_THREAD_INFORMATION))
            throw std::runtime_error("Thread array exceeds process entry");
        if ((raw.ImageName.Length % sizeof(wchar_t)) != 0 || raw.ImageName.Length > raw.ImageName.MaximumLength)
            throw std::runtime_error("Invalid process name length");
        const auto nameAddress = reinterpret_cast<std::uintptr_t>(raw.ImageName.Buffer);
        const auto entryAddress = reinterpret_cast<std::uintptr_t>(buffer.data() + offset);
        const auto nameOffset = nameAddress - entryAddress;
        const auto namesBegin = fixedSize + raw.NumberOfThreads * sizeof(SYSTEM_THREAD_INFORMATION);
        if (raw.ImageName.Length && (nameAddress < entryAddress || nameOffset < namesBegin
            || nameOffset > entrySize || raw.ImageName.Length > entrySize - nameOffset))
            throw std::runtime_error("Process name exceeds entry name area");

        const auto pid = reinterpret_cast<std::uintptr_t>(raw.UniqueProcessId);
        const auto parent = reinterpret_cast<std::uintptr_t>(raw.InheritedFromUniqueProcessId);
        if (pid > std::numeric_limits<std::uint32_t>::max() || parent > std::numeric_limits<std::uint32_t>::max())
            throw std::runtime_error("Process identifier exceeds 32 bits");
        if (raw.KernelTime.QuadPart < 0 || raw.UserTime.QuadPart < 0 || raw.CreateTime.QuadPart < 0
            || raw.ReadTransferCount.QuadPart < 0 || raw.WriteTransferCount.QuadPart < 0)
            throw std::runtime_error("Negative process counter");

        ProcessRecord record;
        record.created100ns = static_cast<std::uint64_t>(raw.CreateTime.QuadPart);
        record.identity = {boot, static_cast<std::uint32_t>(pid),
            raw.CreateTime.QuadPart ? GenerationKind::creationTime : GenerationKind::missing,
            static_cast<std::uint64_t>(raw.CreateTime.QuadPart)};
        record.parentPid = static_cast<std::uint32_t>(parent);
        if (raw.ImageName.Length) {
            record.name.resize(raw.ImageName.Length / sizeof(wchar_t));
            std::memcpy(record.name.data(), raw.ImageName.Buffer, raw.ImageName.Length);
        } else {
            record.name = pid == 0 ? L"System Idle Process" : L"System";
        }
        record.counters = {static_cast<std::uint64_t>(raw.KernelTime.QuadPart),
            static_cast<std::uint64_t>(raw.UserTime.QuadPart),
            static_cast<std::uint64_t>(raw.ReadTransferCount.QuadPart),
            static_cast<std::uint64_t>(raw.WriteTransferCount.QuadPart), raw.WorkingSetSize,
            raw.PrivatePageCount, raw.NumberOfThreads, raw.HandleCount};
        records.push_back(std::move(record));
        if (!raw.NextEntryOffset)
            return records;
        offset += raw.NextEntryOffset;
    }
    throw std::runtime_error("Process chain has no final entry");
}

std::vector<BasicProcessIdentity> parseBasicProcesses(std::span<const std::byte> buffer)
{
    // SYSTEM_BASICPROCESS_INFORMATION in Windows SDK winternl.h, information class 252.
    struct BasicHeader {
        ULONG NextEntryOffset;
        HANDLE UniqueProcessId, InheritedFromUniqueProcessId;
        ULONGLONG SequenceNumber;
        UNICODE_STRING ImageName;
    };
    static_assert(sizeof(BasicHeader) == 48 && offsetof(BasicHeader, SequenceNumber) == 24
        && offsetof(BasicHeader, ImageName) == 32);
    if (buffer.size() < sizeof(BasicHeader)) throw std::runtime_error("Truncated basic process header");
    std::vector<BasicProcessIdentity> records;
    records.reserve(buffer.size() / 128);
    std::size_t offset = 0;
    bool final = false;
    while (offset < buffer.size()) {
        const auto remaining = buffer.size() - offset;
        if (remaining < sizeof(BasicHeader)) throw std::runtime_error("Truncated basic process entry");
        BasicHeader raw{};
        std::memcpy(&raw, buffer.data() + offset, sizeof(raw));
        const auto entrySize = raw.NextEntryOffset ? raw.NextEntryOffset : remaining;
        if (entrySize < sizeof(raw) || entrySize > remaining || raw.NextEntryOffset % alignof(void*) != 0)
            throw std::runtime_error("Invalid basic process entry offset");
        if (raw.ImageName.Length % sizeof(wchar_t) != 0 || raw.ImageName.Length > raw.ImageName.MaximumLength)
            throw std::runtime_error("Invalid basic process name length");
        const auto entryAddress = reinterpret_cast<std::uintptr_t>(buffer.data() + offset);
        const auto nameAddress = reinterpret_cast<std::uintptr_t>(raw.ImageName.Buffer);
        const auto nameOffset = nameAddress - entryAddress;
        if (raw.ImageName.Length && (nameAddress < entryAddress || nameOffset < sizeof(raw) || nameOffset > entrySize
            || raw.ImageName.Length > entrySize - nameOffset)) throw std::runtime_error("Basic process name exceeds entry");
        const auto pid = reinterpret_cast<std::uintptr_t>(raw.UniqueProcessId);
        const auto parent = reinterpret_cast<std::uintptr_t>(raw.InheritedFromUniqueProcessId);
        if (pid > std::numeric_limits<std::uint32_t>::max() || parent > std::numeric_limits<std::uint32_t>::max())
            throw std::runtime_error("Basic process identifier exceeds 32 bits");
        records.push_back({static_cast<std::uint32_t>(pid), static_cast<std::uint32_t>(parent), raw.SequenceNumber});
        if (!raw.NextEntryOffset) { final = true; break; }
        offset += raw.NextEntryOffset;
    }
    if (!final) throw std::runtime_error("Basic process chain has no final entry");
    std::sort(records.begin(), records.end(), [](const auto& a, const auto& b) { return a.pid < b.pid; });
    if (std::adjacent_find(records.begin(), records.end(), [](const auto& a, const auto& b) { return a.pid == b.pid; }) != records.end())
        throw std::runtime_error("Duplicate basic process identifier");
    return records;
}

bool applySequenceIdentities(std::vector<ProcessRecord>& processes,
    std::span<const BasicProcessIdentity> before, std::span<const BasicProcessIdentity> after)
{
    if (processes.empty() || before.empty() || after.empty()) return false;
    const auto ordered = [](auto entries) {
        return std::adjacent_find(entries.begin(), entries.end(), [](const auto& a, const auto& b) { return a.pid >= b.pid; }) == entries.end();
    };
    if (!ordered(before) || !ordered(after)) return false;
    const auto find = [](auto entries, std::uint32_t pid) {
        return std::lower_bound(entries.begin(), entries.end(), pid, [](const auto& entry, auto value) { return entry.pid < value; });
    };
    std::vector<std::uint64_t> sequences;
    std::vector<std::uint8_t> seen(before.size());
    sequences.reserve(processes.size());
    const auto boot = processes.front().identity.boot;
    if (boot.empty()) return false;
    for (const auto& process : processes) {
        const auto& identity = process.identity;
        if (identity.boot != boot || (identity.generationKind != GenerationKind::creationTime && identity.generationKind != GenerationKind::missing)) return false;
        const auto first = find(before, identity.pid);
        if (first == before.end() || first->pid != identity.pid) return false;
        const auto index = static_cast<std::size_t>(first - before.begin());
        const auto second = index < after.size() && after[index].pid == identity.pid ? after.begin() + index : find(after, identity.pid);
        if (second == after.end() || second->pid != identity.pid || first->sequenceNumber != second->sequenceNumber) return false;
        if (!first->sequenceNumber && identity.pid != 0) return false;
        if (seen[index]) return false;
        seen[index] = 1;
        sequences.push_back(first->sequenceNumber);
    }
    for (std::size_t i = 0; i < processes.size(); ++i) {
        processes[i].identity.generationKind = sequences[i] ? GenerationKind::sequenceNumber : GenerationKind::missing;
        processes[i].identity.generation = sequences[i];
    }
    return true;
}

}
