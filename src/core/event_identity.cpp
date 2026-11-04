#include "event_identity.hpp"
#include <algorithm>
#include <limits>

namespace nks {
namespace {
void add(std::uint64_t& value) { if (value != UINT64_MAX) ++value; }
template<class Map, class Key> void makeRoom(Map& map, const Key& key, std::size_t maximum, std::uint64_t& evictions) {
    auto begin = map.lower_bound({key.first, 0});
    auto end = begin;
    std::size_t count = 0;
    while (end != map.end() && end->first.first == key.first) { ++end; ++count; }
    if (count >= EventIdentityIndex::maximumGenerations) { map.erase(begin); add(evictions); }
    if (map.size() >= maximum) { map.erase(map.begin()); add(evictions); }
}
}

bool EventIdentityIndex::process(std::uint32_t pid, std::uint64_t creationTime, std::int64_t qpc, bool start)
{
    if (!pid || !creationTime || qpc < 0) return false;
    const ProcessKey key{pid, creationTime};
    auto found = processes.find(key);
    if (found == processes.end()) {
        makeRoom(processes, key, maximumProcesses, evictions);
        found = processes.emplace(key, Lifetime{}).first;
    }
    auto& time = found->second;
    if (start) {
        if ((time.startSeen && time.start != qpc) || (time.endSeen && qpc > time.end)
            || (time.baselineSeen && qpc > time.baseline)) { add(conflicts); return false; }
        time.start = qpc; time.startSeen = true;
    } else {
        if ((time.endSeen && time.end != qpc) || (time.startSeen && qpc < time.start)) { add(conflicts); return false; }
        time.end = qpc; time.endSeen = true;
    }
    return true;
}

bool EventIdentityIndex::baseline(std::uint32_t pid, std::uint64_t creationTime, std::int64_t qpc)
{
    if (!pid || !creationTime || qpc <= floor) return false;
    const ProcessKey key{pid, creationTime};
    auto found = processes.find(key);
    if (found == processes.end()) {
        makeRoom(processes, key, maximumProcesses, evictions);
        found = processes.emplace(key, Lifetime{}).first;
    }
    auto& time = found->second;
    // A generation listed alive at the snapshot cannot have started after the snapshot completed.
    if (time.startSeen && time.start > qpc) { add(conflicts); return false; }
    if (!time.baselineSeen || qpc < time.baseline) time.baseline = qpc;
    time.baselineSeen = true;
    return true;
}

bool EventIdentityIndex::thread(std::uint32_t pid, std::uint32_t tid, std::int64_t qpc, bool start)
{
    if (!pid || !tid || qpc < 0) return false;
    if (start) {
        const ThreadKey key{tid, qpc};
        const auto existing = threads.find(key);
        if (existing != threads.end()) {
            if (existing->second.pid != pid) { add(conflicts); return false; }
            return true;
        }
        makeRoom(threads, key, maximumThreads, evictions);
        threads.emplace(key, ThreadLifetime{{qpc, 0, true, false}, pid}); return true;
    }
    ThreadLifetime* candidate = nullptr;
    for (auto it = threads.lower_bound({tid, 0}); it != threads.end() && it->first.first == tid; ++it) {
        auto& row = it->second;
        if (row.pid != pid || row.time.start > qpc || row.time.endSeen) continue;
        if (candidate) { add(conflicts); return false; }
        candidate = &row;
    }
    if (candidate) { candidate->time.end = qpc; candidate->time.endSeen = true; }
    // A stop without an observed start does not establish a historical thread lifetime.
    return true;
}

EventIdentityCandidate EventIdentityIndex::processAt(std::uint32_t pid, std::int64_t qpc) const
{
    EventIdentityCandidate result;
    for (auto it = processes.lower_bound({pid, 0}); it != processes.end() && it->first.first == pid; ++it) {
        const auto& time = it->second;
        if (!time.startSeen || qpc < time.start || (time.endSeen && qpc >= time.end)) continue;
        if (result.creationTime) return {};
        result = {pid, it->first.second};
    }
    return result;
}

EventIdentityCandidate EventIdentityIndex::threadAt(std::uint32_t tid, std::int64_t qpc) const
{
    std::uint32_t pid = 0;
    for (auto it = threads.lower_bound({tid, 0}); it != threads.end() && it->first.first == tid; ++it) {
        const auto& row = it->second;
        if (qpc < row.time.start || (row.time.endSeen && qpc >= row.time.end)) continue;
        if (pid) return {};
        pid = row.pid;
    }
    return pid ? processAt(pid, qpc) : EventIdentityCandidate{};
}

EventIdentityCandidate EventIdentityIndex::threadOwnerAt(std::uint32_t tid, std::int64_t qpc) const
{
    if (qpc < 0) return {};
    std::uint32_t pid = 0;
    std::int64_t threadStart = -1;
    for (auto it = threads.lower_bound({tid, 0}); it != threads.end() && it->first.first == tid; ++it) {
        const auto& row = it->second;
        if (qpc == row.time.start || (row.time.endSeen && qpc == row.time.end)) return {};
        if (qpc <= row.time.start || (row.time.endSeen && qpc >= row.time.end)) continue;
        if (pid) return {};
        pid = row.pid; threadStart = row.time.start;
    }
    const auto atStart = processOwnerAt(pid, threadStart), atInit = processOwnerAt(pid, qpc);
    return atStart.creationTime && atStart.creationTime == atInit.creationTime ? atInit : EventIdentityCandidate{};
}

EventIdentityCandidate EventIdentityIndex::processOwnerAt(std::uint32_t pid, std::int64_t point) const
{
    EventIdentityCandidate result;
    if (!pid || point < 0) return result;
    for (auto it = processes.lower_bound({pid, 0}); it != processes.end() && it->first.first == pid; ++it) {
        const auto& time = it->second;
        if ((time.startSeen && point == time.start) || (time.baselineSeen && point == time.baseline)
            || (time.endSeen && point == time.end)) return {};
        if ((time.endSeen && point > time.end) || (time.startSeen && point < time.start)) continue;
        // Only a lifetime witnessed after the last reset is proof; any other possibly alive generation makes the PID ambiguous.
        const bool proven = (time.startSeen && time.start > floor) || (time.baselineSeen && point > time.baseline);
        if (!proven || result.creationTime) return {};
        result = {pid, it->first.second};
    }
    return result;
}

}
