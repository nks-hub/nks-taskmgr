#include "process_traffic.hpp"
#include <algorithm>
#include <cmath>
#include <limits>

namespace nks {
namespace {
bool add(std::uint64_t& value, std::uint64_t amount = 1) {
    const bool overflow = amount > UINT64_MAX - value;
    value += (std::min)(amount, UINT64_MAX - value); return overflow;
}
constexpr std::int64_t reorderDelayNs = 2'000'000'000;
}

void ProcessTrafficStore::initialize(std::int64_t value)
{
    invalidate(false); add(info.evictedSamples, history.size()); history.clear(); frequency = value; newestQpc = 0;
    info.diskState = info.networkState = info.identityProviderState = SampleState::unavailable;
}
void ProcessTrafficStore::providers(SampleState disk, SampleState network, SampleState identity)
{
    info.diskState = disk; info.networkState = network; info.identityProviderState = identity;
}
void ProcessTrafficStore::dropped(const ProcessTrafficObservation& observation)
{
    add(info.pendingDropped); add(info.unknownEvents); add(info.unknownBytes, observation.bytes);
}
void ProcessTrafficStore::observe(ProcessTrafficObservation observation, const EventIdentityIndex& identities)
{
    if (frequency <= 0 || observation.qpc < 0 || observation.observedAtNs <= 0 || info.attributionEpoch == UINT64_MAX) { dropped(observation); return; }
    drain(observation.observedAtNs, identities);
    if (pending.size() == maximumPending) { dropped(pending.front()); pending.pop_front(); }
    try { pending.push_back(observation); }
    catch (const std::bad_alloc&) { dropped(observation); }
    if (observation.source == ProcessTrafficSource::physicalDisk) info.diskState = SampleState::gap;
    else info.networkState = SampleState::gap;
}
void ProcessTrafficStore::drain(std::int64_t nowNs, const EventIdentityIndex& identities, bool finish)
{
    while (!pending.empty()) {
        const auto observation = pending.front();
        if (!finish && (nowNs < observation.observedAtNs || nowNs - observation.observedAtNs < reorderDelayNs)) break;
        const auto candidate = observation.source == ProcessTrafficSource::physicalDisk
            ? identities.threadAt(observation.pidOrTid, observation.qpc) : identities.processAt(observation.pidOrTid, observation.qpc);
        try { commit(observation, candidate); }
        catch (const std::bad_alloc&) { dropped(observation); }
        pending.pop_front();
    }
}
void ProcessTrafficStore::commit(const ProcessTrafficObservation& observation, EventIdentityCandidate candidate)
{
    if (sequence == UINT64_MAX) { dropped(observation); return; }
    const auto start = observation.qpc / frequency * frequency;
    if (start > INT64_MAX - frequency) { dropped(observation); return; }
    const bool unknown = !candidate.creationTime;
    const auto pid = unknown && observation.source != ProcessTrafficSource::physicalDisk ? observation.pidOrTid : candidate.pid;
    // Unresolved observations never collapse several PID lifetimes into one process row.
    const Key key{info.attributionEpoch, start, pid, candidate.creationTime, observation.source, observation.direction,
        observation.ipv6, unknown ? sequence + 1 : 0};
    auto found = history.find(key);
    if (found == history.end()) {
        if (history.size() == maximumSamples) {
            // The key starts with epoch and QPC window, so eviction stays logarithmic.
            history.erase(history.begin()); add(info.evictedSamples);
        }
        ProcessTrafficSample sample;
        sample.sampleId = ++sequence; sample.startQpc = start; sample.endQpc = start + frequency;
        sample.pid = pid; sample.generation = candidate.creationTime;
        sample.identityKind = unknown ? GenerationKind::missing : GenerationKind::creationTime;
        sample.attribution = unknown ? ProcessTrafficAttribution::unknown : ProcessTrafficAttribution::correlated;
        sample.source = observation.source; sample.direction = observation.direction; sample.attributionEpoch = info.attributionEpoch;
        if (observation.ipv6) sample.flags |= trafficIpv6;
        sample.measurement = measurementMetadata(MeasurementUnit::bytesPerSecond, MeasurementNormalization::none,
            MeasurementBasis::elapsedDelta, observation.observedAtNs);
        setMeasurementInterval(sample.measurement, 1'000'000'000, true);
        found = history.emplace(key, Stored{sample}).first;
    }
    auto& stored = found->second; auto& sample = stored.sample;
    if (add(sample.observedBytes, observation.bytes) | add(sample.operations)) sample.flags |= trafficCounterOverflow;
    sample.observedAtNs = (std::max)(sample.observedAtNs, observation.observedAtNs);
    sample.measurement.observedAtNs = sample.observedAtNs;
    sample.observedBytesPerSecond = static_cast<double>(sample.observedBytes);
    if (observation.qpc < newestQpc) { sample.flags |= trafficLate; add(info.lateEvents); }
    newestQpc = (std::max)(newestQpc, observation.qpc); stored.dirty = true;
    if (unknown) { add(info.unknownEvents); add(info.unknownBytes, observation.bytes); }
}
void ProcessTrafficStore::invalidate(bool conflict)
{
    while (!pending.empty()) { dropped(pending.front()); pending.pop_front(); }
    if (conflict) add(info.identityConflicts);
    add(info.attributionEpoch);
    for (auto& [key, stored] : history) {
        (void)key;
        if (!conflict || stored.sample.attribution == ProcessTrafficAttribution::unknown) continue;
        stored.sample.flags |= trafficIdentityConflict;
        stored.sample.identityKind = GenerationKind::missing; stored.sample.generation = 0;
        stored.sample.attribution = ProcessTrafficAttribution::unknown; stored.dirty = true;
    }
}
ProcessTrafficBatch ProcessTrafficStore::read(std::int64_t nowNs, const EventIdentityIndex& identities)
{
    drain(nowNs, identities);
    ProcessTrafficBatch batch;
    batch.samples.reserve(maximumFrameSamples);
    for (auto& [key, stored] : history) {
        (void)key;
        const bool open = stored.sample.endQpc > newestQpc;
        if ((!stored.dirty && stored.publishedOpen == open) || batch.samples.size() == maximumFrameSamples) continue;
        auto sample = stored.sample;
        if (open) sample.flags |= trafficWindowOpen;
        batch.samples.push_back(sample); stored.dirty = false; stored.publishedOpen = open;
    }
    info.observedAtNs = nowNs; info.budgetBytesUsed = eventBudgetUsed.load(); batch.info = info;
    return batch;
}

}
