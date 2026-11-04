#include "process_traffic.hpp"
#include <algorithm>
#include <cmath>
#include <iterator>
#include <limits>

namespace nks {
namespace {
bool add(std::uint64_t& value, std::uint64_t amount = 1) {
    const bool overflow = amount > UINT64_MAX - value;
    value += (std::min)(amount, UINT64_MAX - value); return overflow;
}
constexpr std::int64_t reorderDelayNs = 2'000'000'000;
// Loss polling (1 s) and the reorder delay bound when a committed network window can still be revoked.
constexpr std::int64_t settledWindows = 10;
bool attributed(ProcessTrafficAttribution value) {
    return value == ProcessTrafficAttribution::issuingThreadOwner || value == ProcessTrafficAttribution::direct;
}
}

void ProcessTrafficStore::initialize(std::int64_t value)
{
    invalidate(false); add(info.evictedSamples, history.size()); history.clear(); frequency = value;
    newestQpc = newestObservedQpc = epochFloorQpc = nextInitiationSweep = -1;
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
    const bool disk = observation.source == ProcessTrafficSource::physicalDisk && observation.irp;
    if (disk && metadataIsLate(observation.qpc)) { invalidate(true); dropped(observation); return; }
    newestObservedQpc = (std::max)(newestObservedQpc, observation.qpc);
    if (disk && (observation.qpc <= epochFloorQpc || !diskCompletion(observation))) { dropped(observation); return; }
    const auto epoch = info.attributionEpoch;
    drain(observation.observedAtNs, identities);
    if (epoch != info.attributionEpoch) { dropped(observation); return; }
    if (pending.size() == maximumPending) { invalidate(false); dropped(observation); return; }
    try { pending.push_back(observation); }
    catch (const std::bad_alloc&) { invalidate(false); dropped(observation); }
    if (observation.source == ProcessTrafficSource::physicalDisk) info.diskState = SampleState::gap;
    else info.networkState = SampleState::gap;
}

void ProcessTrafficStore::expireInitiations(bool full)
{
    const auto horizon = newestObservedQpc - maximumInitiationAgeSeconds * frequency;
    if (!full && horizon <= nextInitiationSweep) return;
    nextInitiationSweep = newestObservedQpc;
    // A completion that still arrives for an expired INIT stays unknown; only the stale INIT is lost.
    add(info.pendingDropped, std::erase_if(initiations, [horizon](const auto& entry) { return entry.first.second < horizon; }));
}

bool ProcessTrafficStore::evidenceRoom()
{
    expireInitiations(initiations.size() + completions.size() >= maximumDiskEvidence);
    if (initiations.size() + completions.size() < maximumDiskEvidence) return true;
    invalidate(false); add(info.pendingDropped); return false;
}

void ProcessTrafficStore::diskInitiated(ProcessDiskInitiation initiation, const EventIdentityIndex& identities)
{
    if (frequency <= 0 || initiation.qpc < 0 || initiation.observedAtNs <= 0 || !initiation.irp || !initiation.issuingTid
        || (initiation.direction != ProcessTrafficDirection::read && initiation.direction != ProcessTrafficDirection::write)
        || info.attributionEpoch == UINT64_MAX) { invalidate(false); add(info.pendingDropped); return; }
    if (metadataIsLate(initiation.qpc)) { invalidate(true); add(info.pendingDropped); return; }
    newestObservedQpc = (std::max)(newestObservedQpc, initiation.qpc);
    if (initiation.qpc <= epochFloorQpc) { add(info.pendingDropped); return; }
    const DiskKey key{initiation.irp, initiation.qpc};
    if (initiations.contains(key)) { invalidate(true); add(info.pendingDropped); return; }
    if (!evidenceRoom()) return;
    try { initiations.emplace(key, initiation); }
    catch (const std::bad_alloc&) { invalidate(false); add(info.pendingDropped); return; }
    drain(initiation.observedAtNs, identities);
}

bool ProcessTrafficStore::diskCompletion(const ProcessTrafficObservation& observation)
{
    if (observation.direction != ProcessTrafficDirection::read && observation.direction != ProcessTrafficDirection::write) {
        invalidate(true); return false;
    }
    const DiskKey key{observation.irp, observation.qpc};
    if (completions.contains(key)) { invalidate(true); return false; }
    if (!evidenceRoom()) return false;
    try { completions.emplace(key, observation.direction); }
    catch (const std::bad_alloc&) { invalidate(false); return false; }
    return true;
}

bool ProcessTrafficStore::diskCandidate(const ProcessTrafficObservation& observation, const EventIdentityIndex& identities,
    EventIdentityCandidate& candidate, DiskKey& usedInit)
{
    const auto completion = completions.find({observation.irp, observation.qpc});
    if (completion == completions.end()) return false;
    std::int64_t previous = -1;
    const auto closed = closedDiskIrps.find(observation.irp);
    if (closed != closedDiskIrps.end() && closed->second < observation.qpc) previous = closed->second;
    if (completion != completions.begin()) {
        const auto before = std::prev(completion);
        if (before->first.first == observation.irp) previous = (std::max)(previous, before->first.second);
    }
    const ProcessDiskInitiation* initiation = nullptr;
    // Observed completions delimit reuse even before either operation leaves the 2 s queue.
    for (auto it = initiations.lower_bound({observation.irp, previous});
        it != initiations.end() && it->first.first == observation.irp && it->first.second <= observation.qpc; ++it) {
        if (it->first.second <= previous || it->first.second == observation.qpc || initiation) { invalidate(true); return false; }
        initiation = &it->second;
    }
    if (!initiation) return true;
    if (initiation->direction != observation.direction) { invalidate(true); return false; }
    usedInit = {observation.irp, initiation->qpc};
    candidate = identities.threadOwnerAt(initiation->issuingTid, initiation->qpc);
    return true;
}

void ProcessTrafficStore::consumeDiskEvidence(const ProcessTrafficObservation& observation, const DiskKey& usedInit)
{
    if (usedInit.first) initiations.erase(usedInit);
    completions.erase({observation.irp, observation.qpc});
    auto closed = closedDiskIrps.find(observation.irp);
    if (closed == closedDiskIrps.end()) {
        // Evicted closed timestamps remain covered by the committed QPC watermark.
        if (closedDiskIrps.size() == maximumClosedDiskIrps) closedDiskIrps.erase(closedDiskIrps.begin());
        closedDiskIrps.emplace(observation.irp, observation.qpc);
    } else closed->second = (std::max)(closed->second, observation.qpc);
}

void ProcessTrafficStore::drain(std::int64_t nowNs, const EventIdentityIndex& identities, bool finish)
{
    while (!pending.empty()) {
        const auto observation = pending.front();
        if (!finish && (nowNs < observation.observedAtNs || nowNs - observation.observedAtNs < reorderDelayNs)) break;
        pending.pop_front();
        EventIdentityCandidate candidate;
        DiskKey usedInit{};
        auto attribution = ProcessTrafficAttribution::correlated;
        if (observation.source == ProcessTrafficSource::physicalDisk && observation.irp) {
            if (!diskCandidate(observation, identities, candidate, usedInit)) { dropped(observation); continue; }
            attribution = ProcessTrafficAttribution::issuingThreadOwner;
        } else if (observation.source == ProcessTrafficSource::physicalDisk) candidate = identities.threadAt(observation.pidOrTid, observation.qpc);
        else {
            // The TCP/UDP payload PID names the endpoint owner; the ledger proves which generation held that PID.
            candidate = identities.processOwnerAt(observation.pidOrTid, observation.qpc);
            attribution = ProcessTrafficAttribution::direct;
        }
        bool committed = false;
        try {
            committed = commit(observation, candidate, attribution);
            if (committed && observation.source == ProcessTrafficSource::physicalDisk && observation.irp)
                consumeDiskEvidence(observation, usedInit);
        }
        catch (const std::bad_alloc&) { invalidate(false); if (!committed) dropped(observation); }
    }
}
bool ProcessTrafficStore::commit(const ProcessTrafficObservation& observation, EventIdentityCandidate candidate, ProcessTrafficAttribution attribution)
{
    if (sequence == UINT64_MAX) { dropped(observation); return false; }
    const auto start = observation.qpc / frequency * frequency;
    if (start > INT64_MAX - frequency) { dropped(observation); return false; }
    const bool unknown = !candidate.creationTime;
    if (unknown) attribution = ProcessTrafficAttribution::unknown;
    const auto pid = unknown && observation.source != ProcessTrafficSource::physicalDisk ? observation.pidOrTid : candidate.pid;
    // Unresolved observations never collapse several PID lifetimes into one process row.
    const Key key{info.attributionEpoch, start, pid, candidate.creationTime, observation.source, observation.direction,
        observation.ipv6, attribution, unknown ? sequence + 1 : 0};
    auto found = history.find(key);
    if (found == history.end()) {
        if (history.size() == maximumSamples) {
            auto victim = history.begin();
            const auto& old = victim->second.sample;
            const bool settled = old.attribution == ProcessTrafficAttribution::direct
                && old.endQpc <= newestQpc - settledWindows * frequency;
            if ((attributed(old.attribution) && !settled) || victim->second.revocationPending) {
                invalidate(false);
                victim = std::find_if(history.begin(), history.end(), [](const auto& entry) { return !entry.second.revocationPending; });
                if (victim != history.end()) { history.erase(victim); add(info.evictedSamples); }
                dropped(observation); return false;
            }
            history.erase(victim); add(info.evictedSamples);
        }
        ProcessTrafficSample sample;
        sample.sampleId = ++sequence; sample.startQpc = start; sample.endQpc = start + frequency;
        sample.pid = pid; sample.generation = candidate.creationTime;
        sample.identityKind = unknown ? GenerationKind::missing : GenerationKind::creationTime;
        sample.attribution = attribution;
        if (attributed(attribution)) sample.identityState = SampleState::valid;
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
    return true;
}
void ProcessTrafficStore::invalidate(bool conflict)
{
    while (!pending.empty()) { dropped(pending.front()); pending.pop_front(); }
    if (conflict) add(info.identityConflicts);
    epochFloorQpc = (std::max)(epochFloorQpc, newestObservedQpc);
    initiations.clear(); completions.clear(); closedDiskIrps.clear();
    add(info.attributionEpoch);
    for (auto& [key, stored] : history) {
        (void)key;
        if (stored.sample.attribution == ProcessTrafficAttribution::unknown
            || (!conflict && !attributed(stored.sample.attribution))) continue;
        if (conflict) stored.sample.flags |= trafficIdentityConflict;
        stored.revocationPending |= stored.published;
        stored.sample.flags |= trafficPartial; stored.sample.state = SampleState::gap;
        stored.sample.pid = 0; stored.sample.identityState = SampleState::unavailable;
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
        stored.published = true; stored.revocationPending = false;
    }
    info.observedAtNs = nowNs; info.budgetBytesUsed = eventBudgetUsed.load(); batch.info = info;
    return batch;
}

}
