#pragma once

#include "event_identity_budget.hpp"
#include <cstdint>
#include <map>

namespace nks {

struct EventIdentityCandidate { std::uint32_t pid{}; std::uint64_t creationTime{}; };

class EventIdentityIndex {
public:
    static constexpr std::size_t maximumProcesses = 8192, maximumThreads = 16384, maximumGenerations = 8;
    // A direct CreateTime remains exact even when the surrounding event stream is incomplete.
    bool process(std::uint32_t pid, std::uint64_t creationTime, std::int64_t qpc, bool start);
    bool thread(std::uint32_t pid, std::uint32_t tid, std::int64_t qpc, bool start);
    /// Records a generation listed alive by a process snapshot that completed at qpc.
    bool baseline(std::uint32_t pid, std::uint64_t creationTime, std::int64_t qpc);
    EventIdentityCandidate processAt(std::uint32_t pid, std::int64_t qpc) const;
    /// The unique generation alive strictly inside its witnessed lifetime, which began after the last ledger reset.
    EventIdentityCandidate processOwnerAt(std::uint32_t pid, std::int64_t qpc) const;
    EventIdentityCandidate threadAt(std::uint32_t tid, std::int64_t qpc) const;
    EventIdentityCandidate threadOwnerAt(std::uint32_t tid, std::int64_t qpc) const;
    /// Events up to floorQpc may be missing, so lifetimes starting there no longer prove ownership.
    void clear(std::int64_t floorQpc = -1) noexcept { processes.clear(); threads.clear(); floor = floorQpc; }
    std::int64_t resetFloor() const noexcept { return floor; }
    std::uint64_t evictions{}, conflicts{};
private:
    struct Lifetime { std::int64_t start{}, end{}, baseline{}; bool startSeen{}, endSeen{}, baselineSeen{}; };
    struct ThreadLifetime { Lifetime time; std::uint32_t pid{}; };
    using ProcessKey = std::pair<std::uint32_t, std::uint64_t>;
    using ThreadKey = std::pair<std::uint32_t, std::int64_t>;
    std::map<ProcessKey, Lifetime, std::less<ProcessKey>, EventAllocator<std::pair<const ProcessKey, Lifetime>>> processes;
    std::map<ThreadKey, ThreadLifetime, std::less<ThreadKey>, EventAllocator<std::pair<const ThreadKey, ThreadLifetime>>> threads;
    std::int64_t floor{-1};
};

}
