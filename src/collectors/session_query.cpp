#include <windows.h>
#include <wtsapi32.h>
#include "session_query.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <memory>
#include <mutex>
#include <unordered_map>

namespace nks {
namespace {

SampleState failure() noexcept
{
    return GetLastError() == ERROR_ACCESS_DENIED ? SampleState::accessDenied : SampleState::unavailable;
}

SampleState query(DWORD id, WTS_INFO_CLASS kind, std::wstring* text, SessionProtocol* protocol)
{
    LPWSTR raw{};
    DWORD bytes{};
    if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, id, kind, &raw, &bytes)) return failure();
    std::unique_ptr<wchar_t, decltype(&WTSFreeMemory)> held(raw, WTSFreeMemory);
    if (text) {
        text->assign(raw, wcsnlen(raw, bytes / sizeof(wchar_t)));
    } else {
        if (bytes < sizeof(USHORT)) return SampleState::unavailable;
        USHORT value{};
        std::memcpy(&value, raw, sizeof(value));
        *protocol = value <= 2 ? static_cast<SessionProtocol>(value) : SessionProtocol::unknown;
    }
    return SampleState::valid;
}

}

void SessionSum::add(double item) noexcept
{
    if (!std::isfinite(item) || item < 0) return;
    ++known;
    value += item;
    if (!std::isfinite(value)) overflow = true;
}

SessionState sessionState(std::uint32_t wtsState) noexcept
{
    static_assert(WTSActive == 0 && WTSDisconnected == 4 && WTSInit == 9);
    return wtsState <= WTSInit ? static_cast<SessionState>(wtsState) : SessionState::unknown;
}

std::vector<SessionRecord> querySessions(SampleState& state)
{
    WTS_SESSION_INFOW* raw{};
    DWORD count{};
    if (!WTSEnumerateSessionsW(WTS_CURRENT_SERVER_HANDLE, 0, 1, &raw, &count)) {
        state = failure();
        return {};
    }
    std::unique_ptr<WTS_SESSION_INFOW, decltype(&WTSFreeMemory)> held(raw, WTSFreeMemory);
    std::vector<SessionRecord> sessions;
    sessions.reserve(count);
    for (DWORD i = 0; i < count; ++i) {
        auto& session = sessions.emplace_back();
        session.id = raw[i].SessionId;
        session.state = sessionState(static_cast<std::uint32_t>(raw[i].State));
        if (raw[i].pWinStationName) session.station = raw[i].pWinStationName;
        session.infoState = SampleState::valid;
        for (auto result : {query(session.id, WTSUserName, &session.user, nullptr), query(session.id, WTSDomainName, &session.domain, nullptr),
                 query(session.id, WTSClientProtocolType, nullptr, &session.protocol)})
            if (session.infoState == SampleState::valid) session.infoState = result;
        // The client name is optional detail; its failure must not hide the user.
        query(session.id, WTSClientName, &session.client, nullptr);
    }
    state = SampleState::valid;
    return sessions;
}

namespace {
std::mutex cacheLock;
std::vector<SessionRecord> cache;
bool cacheValid{};
}

std::vector<SessionRecord> cachedSessions(SampleState& state)
{
    std::scoped_lock lock(cacheLock);
    if (!cacheValid) {
        auto fresh = querySessions(state);
        if (state != SampleState::valid) return fresh;
        cache = std::move(fresh);
        cacheValid = true;
    }
    state = SampleState::valid;
    return cache;
}

void invalidateSessions() noexcept
{
    std::scoped_lock lock(cacheLock);
    cacheValid = false;
}

bool userless(const SessionRecord& session) noexcept
{
    return session.infoState == SampleState::valid && session.user.empty();
}

void aggregateSessions(std::vector<SessionRecord>& sessions, const std::vector<ProcessRecord>& processes)
{
    std::unordered_map<std::uint32_t, std::size_t> index;
    for (std::size_t i = 0; i < sessions.size(); ++i) index.emplace(sessions[i].id, i);
    for (const auto& process : processes) {
        // The idle process reports idle time as CPU and owns no memory.
        if (process.identity.pid == 0) continue;
        const auto [at, added] = index.emplace(process.sessionId, sessions.size());
        if (added) sessions.emplace_back().id = process.sessionId;
        auto& session = sessions[at->second];
        ++session.processes;
        if (process.cpuPercent.valid()) session.cpuPercent.add(process.cpuPercent.value);
        session.workingSetBytes.add(static_cast<double>(process.counters.workingSetBytes));
        session.privateBytes.add(static_cast<double>(process.counters.privateBytes));
        if (process.readIoBytesPerSecond.valid() && process.writeIoBytesPerSecond.valid())
            session.ioBytesPerSecond.add(process.readIoBytesPerSecond.value + process.writeIoBytesPerSecond.value);
    }
    std::erase_if(sessions, userless);
    std::ranges::sort(sessions, {}, &SessionRecord::id);
}

}
