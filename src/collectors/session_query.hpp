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
    std::wstring station, user, domain;
    std::uint32_t processes{};
    SessionSum cpuPercent, workingSetBytes, privateBytes, ioBytesPerSecond;
};

SessionState sessionState(std::uint32_t wtsState) noexcept;
// Lists the local WTS sessions; state reports whether the enumeration itself succeeded.
std::vector<SessionRecord> querySessions(SampleState& state);
// Adds process totals (without the idle process) and appends sessions that only processes reveal. Sorted by id.
void aggregateSessions(std::vector<SessionRecord>& sessions, const std::vector<ProcessRecord>& processes);

}
