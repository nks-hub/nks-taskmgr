#include <phnt_windows.h>
#include <phnt.h>
#include "process_parser.hpp"

#include <cstring>
#include <limits>
#include <stdexcept>

namespace nks {

std::vector<ProcessRecord> parseProcesses(std::span<const std::byte> buffer, std::uint64_t bootTime)
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
        const auto bufferAddress = reinterpret_cast<std::uintptr_t>(buffer.data());
        if (raw.ImageName.Length && (nameAddress < bufferAddress || nameAddress - bufferAddress > buffer.size()
            || raw.ImageName.Length > buffer.size() - (nameAddress - bufferAddress)))
            throw std::runtime_error("Process name points outside query buffer");

        const auto pid = reinterpret_cast<std::uintptr_t>(raw.UniqueProcessId);
        const auto parent = reinterpret_cast<std::uintptr_t>(raw.InheritedFromUniqueProcessId);
        if (pid > std::numeric_limits<std::uint32_t>::max() || parent > std::numeric_limits<std::uint32_t>::max())
            throw std::runtime_error("Process identifier exceeds 32 bits");
        if (raw.KernelTime.QuadPart < 0 || raw.UserTime.QuadPart < 0 || raw.CreateTime.QuadPart < 0
            || raw.ReadTransferCount.QuadPart < 0 || raw.WriteTransferCount.QuadPart < 0)
            throw std::runtime_error("Negative process counter");

        ProcessRecord record;
        record.identity = {bootTime, static_cast<std::uint32_t>(pid),
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

}
