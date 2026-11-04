#pragma once
#include "core/process.hpp"

#include <cstdint>
#include <string>
#include <vector>

namespace nks {

// Values match WTS_CONNECTSTATE_CLASS; unknown marks a session seen only through its processes.
enum class SessionState : std::uint8_t {
    active, connected, connectQuery, shadow, disconnected, idle, listen, reset, down, init, unknown
};
// Values match WTSClientProtocolType.
enum class SessionProtocol : std::uint8_t { console, ica, rdp, unknown };

// A sum over the session's processes; known counts the members that had a current sample.
struct SessionSum {
    double value{};
    std::uint32_t known{};
    bool overflow{};
    void add(double item) noexcept;
};

struct SessionRecord {
    std::uint32_t id{};
    SessionState state{SessionState::unknown};
    SessionProtocol protocol{SessionProtocol::unknown};
    // State of the user, domain and protocol queries; the first failure wins.
    SampleState infoState{SampleState::unavailable};
    // station is the window station name (Console, RDP-Tcp#N); client is WTSClientName, empty for the console.
    std::wstring station, user, domain, client;
    std::uint32_t processes{};
    SessionSum cpuPercent, workingSetBytes, privateBytes, ioBytesPerSecond;
};

SessionState sessionState(std::uint32_t wtsState) noexcept;
// Lists the local WTS sessions; state reports whether the enumeration itself succeeded.
std::vector<SessionRecord> querySessions(SampleState& state);
// Returns the last successful querySessions result until invalidateSessions is called; failures are not cached.
std::vector<SessionRecord> cachedSessions(SampleState& state);
void invalidateSessions() noexcept;
// True when WTS confirmed that no user is logged on (session 0, listeners); such sessions are not shown as users.
bool userless(const SessionRecord& session) noexcept;
// Adds process totals (without the idle process), appends sessions that only processes reveal and drops userless
// sessions together with their processes. Sorted by id.
void aggregateSessions(std::vector<SessionRecord>& sessions, const std::vector<ProcessRecord>& processes);

}
