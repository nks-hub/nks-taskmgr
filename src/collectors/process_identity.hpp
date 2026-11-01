#pragma once

#include "core/process.hpp"
#include <windows.h>

namespace nks {

enum class ProcessIdentityPreference { creationTime, sequenceNumberWhenSupported };

struct BasicProcessIdentity {
    std::uint32_t pid{}, parentPid{};
    std::uint64_t sequenceNumber{};
};

struct ProcessIdentityResult {
    ProcessIdentity identity;
    SampleState state{SampleState::unavailable};
};

using ProcessHandleQuery = LONG (NTAPI*)(HANDLE, ULONG, void*, ULONG, ULONG*);

// This verifies the held process object, not action permissions or continued liveness.
ProcessIdentityResult queryProcessIdentity(HANDLE process, GenerationKind kind);
ProcessIdentityResult queryProcessIdentity(HANDLE process, GenerationKind kind,
    const BootIdentity& boot, ProcessHandleQuery query);

}
