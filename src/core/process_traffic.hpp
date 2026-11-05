#pragma once

#include "event_identity.hpp"
#include "process.hpp"
#include <deque>
#include <tuple>

namespace nks {

enum class ProcessTrafficAttribution : std::uint32_t { unknown, correlated, direct, issuingThreadOwner = 3 };
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
    std::uint64_t irp{};
    // A pending network entry can hold several consecutive sends of one PID within one window: firstQpc..qpc.
    std::int64_t firstQpc{-1};
    std::uint32_t operations{1};
};
struct ProcessDiskInitiation {
    std::int64_t qpc{}, observedAtNs{};
    std::uint64_t irp{};
    std::uint32_t issuingTid{};
    ProcessTrafficDirection direction{};
};

class ProcessTrafficStore {
public:
    static constexpr std::size_t maximumPending = 8192, maximumSamples = 4096, maximumFrameSamples = 256;
    static constexpr std::size_t maximumDiskEvidence = 16384;
    static constexpr std::size_t maximumClosedDiskIrps = 8192;
    /// An INIT still without a completion this long after it, by the event clock, is dropped as lost evidence.
    static constexpr std::int64_t maximumInitiationAgeSeconds = 60;
    void initialize(std::int64_t frequency);
    void providers(SampleState disk, SampleState network, SampleState identity);
    void observe(ProcessTrafficObservation observation, const EventIdentityIndex& identities);
    void diskInitiated(ProcessDiskInitiation initiation, const EventIdentityIndex& identities);
    void drain(std::int64_t nowNs, const EventIdentityIndex& identities, bool finish = false);
    void invalidate(bool conflict);
    bool metadataIsLate(std::int64_t qpc) const noexcept { return newestQpc >= 0 && qpc <= newestQpc; }
    ProcessTrafficBatch read(std::int64_t nowNs, const EventIdentityIndex& identities);
    ProcessTrafficBatchInfo info;
private:
    using Key = std::tuple<std::uint64_t, std::int64_t, std::uint32_t, std::uint64_t, ProcessTrafficSource, ProcessTrafficDirection, bool, ProcessTrafficAttribution, std::uint64_t>;
    struct Stored { ProcessTrafficSample sample; bool dirty{true}, publishedOpen{}, published{}, revocationPending{}; };
    using DiskKey = std::pair<std::uint64_t, std::int64_t>;
    bool diskCompletion(const ProcessTrafficObservation& observation);
    bool diskCandidate(const ProcessTrafficObservation& observation, const EventIdentityIndex& identities, EventIdentityCandidate& candidate, DiskKey& usedInit);
    void consumeDiskEvidence(const ProcessTrafficObservation& observation, const DiskKey& usedInit);
    bool evidenceRoom();
    void expireInitiations(bool full);
    bool commit(const ProcessTrafficObservation& observation, EventIdentityCandidate candidate, ProcessTrafficAttribution attribution);
    void dropped(const ProcessTrafficObservation& observation);
    std::deque<ProcessTrafficObservation, EventAllocator<ProcessTrafficObservation>> pending;
    std::map<Key, Stored, std::less<Key>, EventAllocator<std::pair<const Key, Stored>>> history;
    std::map<DiskKey, ProcessDiskInitiation, std::less<DiskKey>, EventAllocator<std::pair<const DiskKey, ProcessDiskInitiation>>> initiations;
    std::map<DiskKey, ProcessTrafficDirection, std::less<DiskKey>, EventAllocator<std::pair<const DiskKey, ProcessTrafficDirection>>> completions;
    std::map<std::uint64_t, std::int64_t, std::less<std::uint64_t>, EventAllocator<std::pair<const std::uint64_t, std::int64_t>>> closedDiskIrps;
    std::int64_t frequency{}, newestQpc{-1}, newestObservedQpc{-1}, epochFloorQpc{-1}, nextInitiationSweep{-1};
    std::uint64_t sequence{};
};

}
