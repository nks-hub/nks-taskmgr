#pragma once
#include "process_identity.hpp"
#include "actions_worker/protocol.hpp"
#include <cstddef>
#include <memory>
#include <vector>

namespace nks::broker { class Consent; }
namespace nks::actions {
struct HandleCloser { void operator()(HANDLE value) const noexcept { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); } };
using Handle = std::unique_ptr<void, HandleCloser>;

struct Target {
    ProcessIdentity key;
    std::uint64_t created100ns{}, window{};
};
struct Result {
    Target target;
    std::uint64_t observedCreated100ns{};
    Outcome outcome{Outcome::refused};
    Reason reason{Reason::none};
    DWORD error{};
    LONG ntStatus{};
    std::uint32_t flags{}, priority{};
    std::uint64_t processMask{}, systemMask{};
    std::uint32_t group{};
    DWORD priorityError{ERROR_NO_DATA}, affinityError{ERROR_NO_DATA};
    std::uint32_t windowsPosted{}, windowsFailed{};
};
struct Context {
    DWORD ownPid{}, parentPid{}, session{};
    std::vector<std::byte> user;
    Handle parent;
    ProcessIdentity parentKey;
    Reason denial{Reason::parentUnavailable};
    bool ready{};
    std::vector<DWORD> protectedPids;
    Handle brokerClient;
    ProcessIdentity brokerClientKey;
    bool brokerConsented() const noexcept { return consented; }
private:
    bool consented{};
    friend class nks::broker::Consent;
};

Context actionContext();
bool priorityAllowed(std::uint64_t value) noexcept;
void applyAction(const Context& context, Command command, std::uint64_t argument, std::uint32_t group, Result& result);
void applyHeldAction(const Context& context, Command command, std::uint64_t argument, std::uint32_t group, HANDLE process, Result& result);
}
