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
    EventIdentityCandidate processAt(std::uint32_t pid, std::int64_t qpc) const;
    EventIdentityCandidate threadAt(std::uint32_t tid, std::int64_t qpc) const;
    EventIdentityCandidate threadOwnerAt(std::uint32_t tid, std::int64_t qpc) const;
    void clear() noexcept { processes.clear(); threads.clear(); }
    std::uint64_t evictions{}, conflicts{};
private:
    struct Lifetime { std::int64_t start{}, end{}; bool startSeen{}, endSeen{}; };
    struct ThreadLifetime { Lifetime time; std::uint32_t pid{}; };
    using ProcessKey = std::pair<std::uint32_t, std::uint64_t>;
    using ThreadKey = std::pair<std::uint32_t, std::int64_t>;
    std::map<ProcessKey, Lifetime, std::less<ProcessKey>, EventAllocator<std::pair<const ProcessKey, Lifetime>>> processes;
    std::map<ThreadKey, ThreadLifetime, std::less<ThreadKey>, EventAllocator<std::pair<const ThreadKey, ThreadLifetime>>> threads;
};

}
