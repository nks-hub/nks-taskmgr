#include <windows.h>
#include <wtsapi32.h>
#include "collectors/session_query.hpp"

#include <algorithm>
#include <iostream>
#include <limits>
#include <stdexcept>

namespace {

unsigned checks{};
void require(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
    ++checks;
}

nks::ProcessRecord process(std::uint32_t pid, std::uint32_t session, double cpu, nks::SampleState cpuState = nks::SampleState::valid,
    nks::SampleState ioState = nks::SampleState::valid)
{
    nks::ProcessRecord record;
    record.identity.pid = pid;
    record.sessionId = session;
    record.cpuPercent = {cpu, cpuState};
    record.counters.workingSetBytes = 1000;
    record.counters.privateBytes = 300;
    record.readIoBytesPerSecond = {10, ioState};
    record.writeIoBytesPerSecond = {5, nks::SampleState::valid};
    return record;
}

void stateMapping()
{
    const std::pair<WTS_CONNECTSTATE_CLASS, nks::SessionState> pairs[] = {
        {WTSActive, nks::SessionState::active}, {WTSConnected, nks::SessionState::connected},
        {WTSConnectQuery, nks::SessionState::connectQuery}, {WTSShadow, nks::SessionState::shadow},
        {WTSDisconnected, nks::SessionState::disconnected}, {WTSIdle, nks::SessionState::idle},
        {WTSListen, nks::SessionState::listen}, {WTSReset, nks::SessionState::reset},
        {WTSDown, nks::SessionState::down}, {WTSInit, nks::SessionState::init}};
    for (const auto& [raw, mapped] : pairs) require(nks::sessionState(raw) == mapped, "A WTS state was mapped to the wrong value");
    require(nks::sessionState(10) == nks::SessionState::unknown && nks::sessionState(0xffffffffu) == nks::SessionState::unknown,
        "An undefined WTS state was not reported as unknown");
}

void liveCurrentSession()
{
    DWORD current{};
    require(ProcessIdToSessionId(GetCurrentProcessId(), &current) != FALSE, "The current session is unknown");
    wchar_t name[257]{};
    DWORD length = static_cast<DWORD>(std::size(name));
    require(GetUserNameW(name, &length) != FALSE, "The current user name is unknown");
    nks::SampleState state{nks::SampleState::error};
    auto sessions = nks::querySessions(state);
    require(state == nks::SampleState::valid && !sessions.empty(), "WTS session enumeration failed");
    const auto own = std::ranges::find(sessions, current, &nks::SessionRecord::id);
    require(own != sessions.end(), "The current session is missing");
    require(own->infoState == nks::SampleState::valid, "The current session's own details were not readable");
    require(_wcsicmp(own->user.c_str(), name) == 0, "The current session does not report the current user");
    require(own->state != nks::SessionState::unknown && own->state != nks::SessionState::listen, "The current session state is not a logged-on state");
    require(own->protocol != nks::SessionProtocol::unknown, "The current session protocol is unknown");
    for (const auto& session : sessions)
        require(session.state != nks::SessionState::unknown, "An enumerated session carried an undefined state");
    require(own->station.size() > 0, "The current session has no window station name");
    require(own->protocol != nks::SessionProtocol::rdp || own->client.size() > 0, "A remote session lost its client name");
    require(std::ranges::any_of(sessions, nks::userless), "No userless session (session 0 or a listener) was enumerated");
    auto shown = sessions;
    nks::aggregateSessions(shown, {});
    require(std::ranges::none_of(shown, nks::userless) && std::ranges::find(shown, current, &nks::SessionRecord::id) != shown.end(),
        "Userless sessions stayed visible or the current session was hidden");
    nks::invalidateSessions();
    nks::SampleState cachedState{nks::SampleState::error};
    const auto first = nks::cachedSessions(cachedState);
    require(cachedState == nks::SampleState::valid && std::ranges::find(first, current, &nks::SessionRecord::id) != first.end(), "The session cache missed the current session");
    const auto second = nks::cachedSessions(cachedState);
    require(cachedState == nks::SampleState::valid && second.size() == first.size(), "The session cache changed without invalidation");
    nks::invalidateSessions();
    require(!nks::cachedSessions(cachedState).empty() && cachedState == nks::SampleState::valid, "The session cache did not refill after invalidation");
    std::wcout << L"Current session " << current << L": " << own->domain << L'\\' << own->user << L", state "
        << static_cast<unsigned>(own->state) << L", protocol " << static_cast<unsigned>(own->protocol) << L", " << sessions.size() << L" sessions\n";
}

void completeSums()
{
    std::vector<nks::SessionRecord> sessions(1);
    sessions[0].id = 1; sessions[0].state = nks::SessionState::active; sessions[0].infoState = nks::SampleState::valid; sessions[0].user = L"a";
    nks::aggregateSessions(sessions, {process(0, 1, 90), process(10, 1, 2.5), process(11, 1, 1.5)});
    const auto& s = sessions[0];
    require(sessions.size() == 1 && s.processes == 2, "The idle process or a member was miscounted");
    require(s.cpuPercent.value == 4 && s.cpuPercent.known == 2 && !s.cpuPercent.overflow, "Complete CPU sum is wrong");
    require(s.workingSetBytes.value == 2000 && s.workingSetBytes.known == 2, "Working set sum is wrong");
    require(s.privateBytes.value == 600 && s.privateBytes.known == 2, "Private memory sum is wrong");
    require(s.ioBytesPerSecond.value == 30 && s.ioBytesPerSecond.known == 2, "I/O sum is wrong");
}

void incompleteSums()
{
    std::vector<nks::SessionRecord> sessions(2);
    sessions[0].id = 2; sessions[1].id = 1;
    nks::aggregateSessions(sessions, {process(20, 2, 3), process(21, 2, 0, nks::SampleState::first),
        process(22, 2, 7, nks::SampleState::valid, nks::SampleState::accessDenied), process(30, 5, 1)});
    require(sessions.size() == 3 && sessions[0].id == 1 && sessions[1].id == 2 && sessions[2].id == 5, "Sessions were not sorted or a process-only session was lost");
    const auto& s = sessions[1];
    require(s.processes == 3 && s.cpuPercent.value == 10 && s.cpuPercent.known == 2, "A missing CPU sample was summed as zero");
    require(s.ioBytesPerSecond.value == 30 && s.ioBytesPerSecond.known == 2, "A missing I/O sample was summed as zero");
    require(sessions[0].processes == 0 && sessions[0].cpuPercent.known == 0, "An empty session received members");
    require(sessions[2].state == nks::SessionState::unknown && sessions[2].infoState != nks::SampleState::valid && sessions[2].processes == 1,
        "A session known only from processes looked fully queried");
    nks::SessionSum sum;
    sum.add(std::numeric_limits<double>::max()); sum.add(std::numeric_limits<double>::max());
    require(sum.overflow && sum.known == 2, "Overflow was not reported");
    nks::SessionSum invalid;
    invalid.add(-1); invalid.add(std::numeric_limits<double>::quiet_NaN());
    require(invalid.known == 0 && invalid.value == 0 && !invalid.overflow, "An invalid sample was counted");
}

void hiddenSessions()
{
    std::vector<nks::SessionRecord> sessions(5);
    sessions[0].id = 0; sessions[0].infoState = nks::SampleState::valid; sessions[0].station = L"Services";
    sessions[1].id = 65536; sessions[1].infoState = nks::SampleState::valid; sessions[1].state = nks::SessionState::listen; sessions[1].station = L"RDP-Tcp";
    sessions[2].id = 2; sessions[2].infoState = nks::SampleState::valid; sessions[2].user = L"b"; sessions[2].station = L"RDP-Tcp#3"; sessions[2].client = L"PC-A";
    sessions[3].id = 3; sessions[3].infoState = nks::SampleState::accessDenied;
    sessions[4].id = 4; sessions[4].infoState = nks::SampleState::valid; sessions[4].station = L"Console"; sessions[4].state = nks::SessionState::connected;
    nks::aggregateSessions(sessions, {process(4, 0, 50), process(5, 0, 20), process(40, 2, 1), process(41, 4, 7)});
    require(sessions.size() == 2 && sessions[0].id == 2 && sessions[1].id == 3, "A userless session was shown or an unreadable one was hidden");
    require(sessions[0].processes == 1 && sessions[0].cpuPercent.value == 1 && sessions[0].client == L"PC-A" && sessions[0].station == L"RDP-Tcp#3",
        "Userless processes leaked into a user's sums or the station and client were lost");
    require(sessions[1].processes == 0, "An unreadable session received foreign processes");
    nks::SessionRecord unknown; unknown.id = 7;
    require(!nks::userless(unknown), "A session known only from processes was treated as userless");
}

}

int main()
{
    try {
        stateMapping(); completeSums(); incompleteSums(); hiddenSessions(); liveCurrentSession();
        std::cout << "Session checks passed: " << checks << " assertions\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "Session checks failed: " << error.what() << '\n';
        return 1;
    }
}
