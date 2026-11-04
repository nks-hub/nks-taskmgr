#include <phnt_windows.h>
#include <phnt.h>
#include "process_details.hpp"
#include "boot_identity.hpp"
#include "process_parser.hpp"
#include "process_query.h"
#include <algorithm>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <unordered_set>

namespace nks {
namespace {

constexpr ULONG maximumHandleQueryBytes = 4 * 1024 * 1024;
constexpr ULONG maximumObjectQueryBytes = 256 * 1024;

bool resizeStatus(LONG status)
{
    return status == STATUS_INFO_LENGTH_MISMATCH || status == STATUS_BUFFER_TOO_SMALL || status == STATUS_BUFFER_OVERFLOW;
}

SampleState queryState(LONG status)
{
    if (status == STATUS_ACCESS_DENIED || status == STATUS_PRIVILEGE_NOT_HELD) return SampleState::accessDenied;
    if (status == STATUS_INVALID_INFO_CLASS || status == STATUS_NOT_IMPLEMENTED || status == STATUS_NOT_SUPPORTED) return SampleState::unsupported;
    return SampleState::unavailable;
}

void nativeFailure(ProcessDetailsResult& result, LONG status)
{
    result.ntStatus = status; result.win32Error = RtlNtStatusToDosError(status);
    if (status == STATUS_RETRY || status == STATUS_INSUFFICIENT_RESOURCES || resizeStatus(status)) {
        result.state = details::ResultState::limitExceeded; return;
    }
    const auto state = queryState(status);
    result.state = state == SampleState::accessDenied ? details::ResultState::accessDenied
        : state == SampleState::unsupported ? details::ResultState::unsupported : details::ResultState::unavailable;
}

bool validHandleValue(std::uint64_t value)
{
    // Pseudo handles are not entries in the selected process's handle table.
    return value && value <= static_cast<std::uint64_t>(std::numeric_limits<std::intptr_t>::max())
        && (value & 0xffffffffu) < 0xfffffff0u;
}

bool copyObjectText(const UNICODE_STRING& text, const std::vector<std::byte>& buffer, ULONG used,
    std::size_t headerBytes, bool allowEmpty, std::wstring& value)
{
    if (text.Length % sizeof(wchar_t) || text.Length > text.MaximumLength || text.Length / sizeof(wchar_t) > details::maximumStringChars) return false;
    if (!text.Length) return allowEmpty;
    const auto begin = reinterpret_cast<std::uintptr_t>(buffer.data());
    const auto at = reinterpret_cast<std::uintptr_t>(text.Buffer);
    if (at < begin || at - begin < headerBytes || at - begin > used || text.Length > used - (at - begin) || at % alignof(wchar_t)) return false;
    value.resize(text.Length / sizeof(wchar_t));
    std::memcpy(value.data(), text.Buffer, text.Length);
    for (std::size_t i = 0; i < value.size(); ++i) {
        const auto unit = static_cast<std::uint16_t>(value[i]);
        if (!unit) return false;
        if (unit >= 0xd800 && unit <= 0xdbff) {
            if (++i == value.size() || value[i] < 0xdc00 || value[i] > 0xdfff) return false;
        } else if (unit >= 0xdc00 && unit <= 0xdfff) return false;
    }
    return true;
}

LONG objectText(HANDLE object, OBJECT_INFORMATION_CLASS informationClass, std::wstring& text)
{
    std::vector<std::byte> buffer(1024);
    const auto began = GetTickCount64();
    for (unsigned attempt = 0; attempt < 4; ++attempt) {
        ULONG used = 0;
        const auto status = NtQueryObject(object, informationClass, buffer.data(), static_cast<ULONG>(buffer.size()), &used);
        if (NT_SUCCESS(status)) {
            const auto header = informationClass == ObjectTypeInformation ? sizeof(OBJECT_TYPE_INFORMATION) : sizeof(OBJECT_NAME_INFORMATION);
            if (used < header || used > buffer.size()) return STATUS_DATA_ERROR;
            UNICODE_STRING native{}; std::memcpy(&native, buffer.data(), sizeof(native));
            if (!copyObjectText(native, buffer, used, header, informationClass == ObjectNameInformation, text)) {
                text.clear(); return STATUS_OBJECT_NAME_INVALID;
            }
            return STATUS_SUCCESS;
        }
        if (!resizeStatus(status)) return status;
        if (GetTickCount64() - began > 3000) return STATUS_IO_TIMEOUT;
        const auto next = std::max<std::uint64_t>(used, buffer.size() * 2);
        if (next > maximumObjectQueryBytes) return STATUS_BUFFER_TOO_SMALL;
        buffer.resize(static_cast<std::size_t>(next));
    }
    return STATUS_BUFFER_TOO_SMALL;
}

LONG NTAPI systemQuery(ULONG informationClass, void* buffer, ULONG size, ULONG* used)
{
    return NtQuerySystemInformation(static_cast<SYSTEM_INFORMATION_CLASS>(informationClass), buffer, size, used);
}

struct ProcessBuffer {
    void* data{};
    ULONG capacity{};
    ~ProcessBuffer() { NksFreeProcessBuffer(data); }
};

bool orderIdentities(std::vector<BasicProcessIdentity>& identities)
{
    std::sort(identities.begin(), identities.end(), [](const auto& a, const auto& b) { return a.pid < b.pid; });
    return std::adjacent_find(identities.begin(), identities.end(), [](const auto& a, const auto& b) { return a.pid == b.pid; }) == identities.end();
}

std::uint64_t sequenceAt(const std::vector<BasicProcessIdentity>& identities, std::uint32_t pid)
{
    const auto found = std::lower_bound(identities.begin(), identities.end(), pid, [](const auto& entry, auto id) { return entry.pid < id; });
    return found != identities.end() && found->pid == pid ? found->sequenceNumber : 0;
}

bool verifyBoot(const ProcessDetailsRequest& request, ProcessDetailsResult& result)
{
    const auto boot = queryBootIdentity(systemQuery);
    if (boot.state != SampleState::valid || boot.id.empty()) {
        result.state = boot.state == SampleState::accessDenied ? details::ResultState::accessDenied
            : boot.state == SampleState::unsupported ? details::ResultState::unsupported : details::ResultState::unavailable;
        return false;
    }
    if (boot.id != request.target.boot) { result.state = details::ResultState::targetChanged; result.win32Error = ERROR_RETRY; return false; }
    return true;
}

}

void readProcessHandles(HANDLE process, ProcessDetailsResult& result)
{
    static_assert(offsetof(PROCESS_HANDLE_SNAPSHOT_INFORMATION, Handles) == 16 && sizeof(PROCESS_HANDLE_TABLE_ENTRY_INFO) == 40);
    std::vector<std::byte> buffer(32768);
    const auto began = GetTickCount64();
    for (unsigned attempt = 0; attempt < 8; ++attempt) {
        std::fill(buffer.begin(), buffer.end(), std::byte{});
        ULONG used = 0;
        const auto status = NtQueryInformationProcess(process, ProcessHandleInformation, buffer.data(), static_cast<ULONG>(buffer.size()), &used);
        if (NT_SUCCESS(status)) {
            constexpr auto header = offsetof(PROCESS_HANDLE_SNAPSHOT_INFORMATION, Handles);
            if (used < header || used > buffer.size()) { nativeFailure(result, STATUS_DATA_ERROR); return; }
            ULONG_PTR count{}; std::memcpy(&count, buffer.data(), sizeof(count));
            if (count > details::maximumHandles) { result.state = details::ResultState::limitExceeded; return; }
            if (count > (used - header) / sizeof(PROCESS_HANDLE_TABLE_ENTRY_INFO)) { nativeFailure(result, STATUS_DATA_ERROR); return; }
            if (!count) {
                // Some minimal processes return success without a usable snapshot.
                DWORD observed{};
                if (!GetProcessHandleCount(process, &observed) || observed) { nativeFailure(result, STATUS_UNSUCCESSFUL); return; }
            }
            std::unordered_set<std::uint64_t> values;
            result.handles.reserve(static_cast<std::size_t>(count));
            for (std::size_t i = 0; i < count; ++i) {
                PROCESS_HANDLE_TABLE_ENTRY_INFO entry{};
                std::memcpy(&entry, buffer.data() + header + i * sizeof(entry), sizeof(entry));
                const auto value = static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(entry.HandleValue));
                if (!validHandleValue(value) || !values.insert(value).second) { nativeFailure(result, STATUS_DATA_ERROR); return; }
                result.handles.push_back({value, entry.GrantedAccess, entry.ObjectTypeIndex, entry.HandleAttributes, 0});
            }
            result.state = details::ResultState::ok; return;
        }
        if (!resizeStatus(status)) { nativeFailure(result, status); return; }
        const auto next = std::max<std::uint64_t>(used, buffer.size() * 2);
        if (next > maximumHandleQueryBytes || GetTickCount64() - began > 3000) {
            result.state = details::ResultState::limitExceeded; result.ntStatus = status; return;
        }
        buffer.resize(static_cast<std::size_t>(next));
    }
    result.state = details::ResultState::limitExceeded; result.ntStatus = STATUS_BUFFER_TOO_SMALL;
}

void readProcessHandleInfo(HANDLE process, std::uint64_t value, ProcessDetailsResult& result)
{
    if (!validHandleValue(value)) { result.state = details::ResultState::invalidRequest; return; }
    HANDLE raw = nullptr;
    if (!DuplicateHandle(process, reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(value)), GetCurrentProcess(), &raw, 0, FALSE, 0)) {
        result.win32Error = GetLastError();
        result.state = result.win32Error == ERROR_ACCESS_DENIED ? details::ResultState::accessDenied : details::ResultState::unavailable;
        return;
    }
    DetailHandle duplicate(raw);
    ProcessHandleDetail detail;
    detail.info.value = value;
    // Both queries observe this held duplicate, not an object from an earlier refresh.
    detail.info.typeNtStatus = objectText(duplicate.get(), ObjectTypeInformation, detail.type);
    detail.info.nameNtStatus = objectText(duplicate.get(), ObjectNameInformation, detail.name);
    detail.info.typeState = static_cast<std::uint32_t>(NT_SUCCESS(detail.info.typeNtStatus) ? SampleState::valid : queryState(detail.info.typeNtStatus));
    detail.info.nameState = static_cast<std::uint32_t>(NT_SUCCESS(detail.info.nameNtStatus) ? SampleState::valid : queryState(detail.info.nameNtStatus));
    detail.info.typeChars = static_cast<std::uint32_t>(detail.type.size()); detail.info.nameChars = static_cast<std::uint32_t>(detail.name.size());
    if (detail.info.typeState != static_cast<std::uint32_t>(SampleState::valid)
        || detail.info.nameState != static_cast<std::uint32_t>(SampleState::valid)) result.flags |= details::partialMetrics;
    result.handleInfo = std::move(detail); result.state = details::ResultState::ok;
}

LONG queryObjectText(HANDLE object, bool name, std::wstring& text)
{
    text.clear(); // An unnamed object leaves the output untouched.
    return objectText(object, name ? ObjectNameInformation : ObjectTypeInformation, text);
}

void readProcessOwners(const ProcessDetailsRequest& request, ProcessDetailsResult& result)
{
    if (!verifyBoot(request, result)) return;
    ProcessBuffer full, basic;
    NksProcessQueryBudget budget{NKS_PROCESS_QUERY_ATTEMPTS, 0};
    bool sequence = request.target.generationKind == GenerationKind::sequenceNumber;
    const auto began = GetTickCount64();
    try {
        std::vector<BasicProcessIdentity> before, after;
        ULONG used = 0;
        if (sequence) {
            const auto status = NksQueryProcessBufferBudgeted(systemQuery, SystemBasicProcessInformation, &basic.data, &basic.capacity, &used, &budget);
            if (status == STATUS_INVALID_INFO_CLASS || status == STATUS_NOT_SUPPORTED || status == STATUS_NOT_IMPLEMENTED) {
                sequence = false; budget.allocated_bytes -= basic.capacity;
                NksFreeProcessBuffer(basic.data); basic.data = nullptr; basic.capacity = 0;
            } else if (!NT_SUCCESS(status)) { nativeFailure(result, status); return; }
            else before = parseBasicProcesses({static_cast<const std::byte*>(basic.data), used});
        }
        auto status = NksQueryProcessBufferBudgeted(systemQuery, SystemProcessInformation, &full.data, &full.capacity, &used, &budget);
        if (!NT_SUCCESS(status)) { nativeFailure(result, status); return; }
        auto processes = parseProcesses({static_cast<const std::byte*>(full.data), used}, request.target.boot);
        if (sequence) {
            status = NksQueryProcessBufferBudgeted(systemQuery, SystemBasicProcessInformation, &basic.data, &basic.capacity, &used, &budget);
            if (!NT_SUCCESS(status)) { nativeFailure(result, status); return; }
            after = parseBasicProcesses({static_cast<const std::byte*>(basic.data), used});
            if (!orderIdentities(before) || !orderIdentities(after)) { nativeFailure(result, STATUS_DATA_ERROR); return; }
        }
        std::unordered_set<std::uint32_t> pids;
        std::size_t payload = 0;
        for (auto& process : processes) {
            if (GetTickCount64() - began > 3000) { result.state = details::ResultState::limitExceeded; return; }
            auto id = process.identity;
            if (!pids.insert(id.pid).second) { nativeFailure(result, STATUS_DATA_ERROR); return; }
            if (sequence) {
                const auto first = sequenceAt(before, id.pid), second = sequenceAt(after, id.pid);
                if (!first || first != second) { result.flags |= details::partialMetrics; continue; }
                id.generationKind = GenerationKind::sequenceNumber; id.generation = first;
            }
            if (!id.pid || !id.generation || id.boot != request.target.boot
                || (id.generationKind != GenerationKind::creationTime && id.generationKind != GenerationKind::sequenceNumber)) {
                result.flags |= details::partialMetrics; continue;
            }
            const auto rowBytes = sizeof(details::Owner) + process.name.size() * sizeof(wchar_t);
            if (process.name.size() > details::maximumStringChars || rowBytes > details::maximumResponsePayload - payload
                || result.owners.size() == details::maximumOwners) { result.state = details::ResultState::limitExceeded; return; }
            payload += rowBytes;
            result.owners.push_back({id, std::move(process.name)});
        }
        if (!verifyBoot(request, result)) { result.owners.clear(); return; }
        if (result.owners.empty()) { result.state = details::ResultState::unavailable; return; }
        result.state = details::ResultState::ok;
    } catch (const std::runtime_error&) { nativeFailure(result, STATUS_DATA_ERROR); }
}

}
