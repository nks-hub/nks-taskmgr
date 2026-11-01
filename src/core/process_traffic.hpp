#pragma once

#include "event_identity.hpp"
#include "process.hpp"
#include <deque>
#include <tuple>

namespace nks {

enum class ProcessTrafficAttribution : std::uint32_t { unknown, correlated, direct };
enum class ProcessTrafficSource : std::uint32_t { physicalDisk = 1, tcp = 2, udp = 3 };
enum class ProcessTrafficDirection : std::uint32_t { read = 1, write = 2, sent = 3, received = 4 };
enum ProcessTrafficFlag : std::uint32_t {
    trafficPartial = 1, trafficIpv6 = 2, trafficCounterOverflow = 4, trafficLate = 8,
    trafficIdentityConflict = 16, trafficWindowOpen = 32
};
struct ProcessTrafficSample {
    std::uint64_t sampleId{};
    std::int64_t startQpc{}, endQpc{}, observedAtNs{};
    std::uint32_t pid{};
    GenerationKind identityKind{GenerationKind::missing};
    std::uint64_t generation{};
    SampleState identityState{SampleState::unavailable};
    ProcessTrafficAttribution attribution{};
    ProcessTrafficSource source{};
    ProcessTrafficDirection direction{};
    std::uint64_t observedBytes{}, operations{};
    double observedBytesPerSecond{};
    SampleState state{SampleState::gap};
    std::uint32_t flags{trafficPartial};
    std::uint64_t attributionEpoch{};
    MeasurementMetadata measurement;
};
struct ProcessTrafficBatchInfo {
    SampleState diskState{SampleState::unavailable}, networkState{SampleState::unavailable}, identityProviderState{SampleState::unavailable};
    std::uint32_t flags{};
    std::int64_t observedAtNs{};
    std::uint64_t attributionEpoch{}, unknownEvents{}, unknownBytes{}, evictedSamples{}, pendingDropped{}, lateEvents{}, identityConflicts{};
    std::uint64_t budgetBytesUsed{}, budgetLimit{eventBudgetLimit};
};
struct ProcessTrafficBatch { ProcessTrafficBatchInfo info; EventVector<ProcessTrafficSample> samples; };
struct ProcessTrafficObservation {
    std::int64_t qpc{}, observedAtNs{};
    std::uint32_t pidOrTid{}, bytes{};
    ProcessTrafficSource source{};
    ProcessTrafficDirection direction{};
    bool ipv6{};
};

class ProcessTrafficStore {
public:
    static constexpr std::size_t maximumPending = 8192, maximumSamples = 4096, maximumFrameSamples = 256;
    void initialize(std::int64_t frequency);
    void providers(SampleState disk, SampleState network, SampleState identity);
    void observe(ProcessTrafficObservation observation, const EventIdentityIndex& identities);
    void drain(std::int64_t nowNs, const EventIdentityIndex& identities, bool finish = false);
    void invalidate(bool conflict);
    bool metadataIsLate(std::int64_t qpc) const noexcept { return newestQpc > 0 && qpc <= newestQpc; }
    ProcessTrafficBatch read(std::int64_t nowNs, const EventIdentityIndex& identities);
    ProcessTrafficBatchInfo info;
private:
    using Key = std::tuple<std::uint64_t, std::int64_t, std::uint32_t, std::uint64_t, ProcessTrafficSource, ProcessTrafficDirection, bool, std::uint64_t>;
    struct Stored { ProcessTrafficSample sample; bool dirty{true}, publishedOpen{}; };
    void commit(const ProcessTrafficObservation& observation, EventIdentityCandidate candidate);
    void dropped(const ProcessTrafficObservation& observation);
    std::deque<ProcessTrafficObservation, EventAllocator<ProcessTrafficObservation>> pending;
    std::map<Key, Stored, std::less<Key>, EventAllocator<std::pair<const Key, Stored>>> history;
    std::int64_t frequency{}, newestQpc{};
    std::uint64_t sequence{};
};

}
