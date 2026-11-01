#include <phnt_windows.h>
#include <phnt.h>
#include "process_identity.hpp"
#include "boot_identity.hpp"
#include <limits>

namespace nks {
namespace {
LONG NTAPI systemQuery(ULONG kind, void* buffer, ULONG size, ULONG* returned)
{
    return NtQuerySystemInformation(static_cast<SYSTEM_INFORMATION_CLASS>(kind), buffer, size, returned);
}
LONG NTAPI handleQuery(HANDLE process, ULONG kind, void* buffer, ULONG size, ULONG* returned)
{
    return NtQueryInformationProcess(process, static_cast<PROCESSINFOCLASS>(kind), buffer, size, returned);
}
SampleState queryState(LONG status)
{
    if (status == STATUS_ACCESS_DENIED || status == STATUS_PRIVILEGE_NOT_HELD) return SampleState::accessDenied;
    if (status == STATUS_INVALID_INFO_CLASS || status == STATUS_NOT_IMPLEMENTED || status == STATUS_NOT_SUPPORTED)
        return SampleState::unsupported;
    return SampleState::error;
}
}

ProcessIdentityResult queryProcessIdentity(HANDLE process, GenerationKind kind,
    const BootIdentity& boot, ProcessHandleQuery query)
{
    if (kind == GenerationKind::missing) return {{}, SampleState::unavailable};
    if (!process || !query || (kind != GenerationKind::creationTime && kind != GenerationKind::sequenceNumber)) return {{}, SampleState::error};
    if (boot.state != SampleState::valid) return {{}, boot.state};
    if (boot.id.empty()) return {{}, SampleState::error};
    PROCESS_BASIC_INFORMATION basic{};
    ULONG used = 0;
    auto status = query(process, ProcessBasicInformation, &basic, sizeof(basic), &used);
    if (!NT_SUCCESS(status)) return {{}, queryState(status)};
    const auto pid = reinterpret_cast<std::uintptr_t>(basic.UniqueProcessId);
    if (used != sizeof(basic) || pid > std::numeric_limits<std::uint32_t>::max()) return {{}, SampleState::error};
    std::uint64_t generation = 0;
    used = 0;
    if (kind == GenerationKind::sequenceNumber) {
        status = query(process, ProcessSequenceNumber, &generation, sizeof(generation), &used);
        if (!NT_SUCCESS(status)) return {{}, queryState(status)};
        if (used != sizeof(generation)) return {{}, SampleState::error};
    } else {
        KERNEL_USER_TIMES times{};
        status = query(process, ProcessTimes, &times, sizeof(times), &used);
        if (!NT_SUCCESS(status)) return {{}, queryState(status)};
        if (used != sizeof(times) || times.CreateTime.QuadPart < 0) return {{}, SampleState::error};
        generation = static_cast<std::uint64_t>(times.CreateTime.QuadPart);
    }
    if (!generation) return {{}, SampleState::unavailable};
    return {{boot.id, static_cast<std::uint32_t>(pid), kind, generation}, SampleState::valid};
}

ProcessIdentityResult queryProcessIdentity(HANDLE process, GenerationKind kind)
{
    return queryProcessIdentity(process, kind, queryBootIdentity(systemQuery), handleQuery);
}

}
