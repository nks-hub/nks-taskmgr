#include "process_events.hpp"
#include <windows.h>
#include <algorithm>
#include <limits>

namespace nks {
namespace {
void increment(std::uint64_t& value, std::uint64_t amount = 1) {
    value += (std::min)(amount, std::numeric_limits<std::uint64_t>::max() - value);
}
std::int64_t nowQpc() { LARGE_INTEGER value{}; QueryPerformanceCounter(&value); return value.QuadPart; }
}

void ProcessEventStore::resync()
{
    info.flags |= eventsResyncRequired; increment(info.resyncVersion);
}

void ProcessEventStore::initialize(BootIdentity boot, std::int64_t qpcFrequency)
{
    std::lock_guard lock(mutex);
    increment(info.queueDropped, records.size()); records.clear(); identities.clear(); transfers.initialize(qpcFrequency);
    info.boot = boot; info.qpcFrequency = qpcFrequency;
    resync();
}

void ProcessEventStore::state(ProcessEventMonitorState value, std::uint32_t error, bool cleanupOutstanding)
{
    std::lock_guard lock(mutex);
    const auto epoch = transfers.info.attributionEpoch, dropped = transfers.info.pendingDropped, evicted = transfers.info.evictedSamples;
    if (!cleanupOutstanding && value != ProcessEventMonitorState::starting && value != ProcessEventMonitorState::idle)
        transfers.drain(measurementTime(std::chrono::steady_clock::now()), identities, true);
    trafficChanges(epoch, dropped, evicted);
    info.state = value; info.win32Error = error;
    if (cleanupOutstanding) info.flags |= eventsCleanupOutstanding;
    else info.flags &= ~eventsCleanupOutstanding;
    if (value != ProcessEventMonitorState::stopping) stopRequestedAt = {};
}

void ProcessEventStore::requestStop()
{
    std::lock_guard lock(mutex);
    if (info.state != ProcessEventMonitorState::starting && info.state != ProcessEventMonitorState::running
        && info.state != ProcessEventMonitorState::stopping && info.state != ProcessEventMonitorState::cleanupFailed) return;
    info.state = ProcessEventMonitorState::stopping; info.flags |= eventsCleanupOutstanding;
    if (stopRequestedAt == std::chrono::steady_clock::time_point{}) { stopRequestedAt = std::chrono::steady_clock::now(); resync(); }
}

void ProcessEventStore::push(ProcessEventRecord record)
{
    std::lock_guard lock(mutex);
    if (sequence == std::numeric_limits<std::uint64_t>::max()) { increment(info.queueDropped); resync(); return; }
    if (records.size() == capacity) { records.pop_front(); increment(info.queueDropped); resync(); }
    record.sequence = ++sequence;
    // Only the provider's payload CreateTime establishes the target's historical lifetime.
    if (record.identityKind == GenerationKind::creationTime && record.generation && record.pid
        && info.boot.state == SampleState::valid && !info.boot.id.empty()) {
        record.identityState = SampleState::valid;
        if (transfers.metadataIsLate(record.timestampQpc)) invalidate(true);
        const auto evictions = identities.evictions, conflicts = identities.conflicts;
        identities.process(record.pid, record.generation, record.timestampQpc, record.kind == ProcessEventKind::started);
        identityChanges(evictions, conflicts);
    } else {
        record.identityKind = GenerationKind::missing; record.generation = 0; record.identityState = SampleState::unavailable;
        increment(info.ambiguous);
    }
    if (record.imageName.size() > maximumNameChars) {
        record.imageName.clear(); record.flags = (record.flags & ~eventImageName) | eventPartial;
        increment(info.decodeErrors); resync();
    }
    try { records.push_back(std::move(record)); }
    catch (const std::bad_alloc&) { increment(info.queueDropped); resync(); }
}

void ProcessEventStore::invalidate(bool conflict) { identities.clear(nowQpc()); transfers.invalidate(conflict); resync(); }
void ProcessEventStore::identityChanges(std::uint64_t evictions, std::uint64_t conflicts) {
    if (identities.conflicts != conflicts) invalidate(true);
    else if (identities.evictions != evictions) invalidate(false);
}
void ProcessEventStore::trafficChanges(std::uint64_t epoch, std::uint64_t dropped, std::uint64_t evicted) {
    if (epoch != transfers.info.attributionEpoch) { identities.clear(nowQpc()); resync(); }
    else if (dropped != transfers.info.pendingDropped || evicted != transfers.info.evictedSamples) resync();
}
void ProcessEventStore::thread(std::uint32_t pid, std::uint32_t tid, std::int64_t qpc, bool start) {
    std::lock_guard lock(mutex);
    if (info.boot.state != SampleState::valid || info.boot.id.empty()) return;
    if (transfers.metadataIsLate(qpc)) invalidate(true);
    const auto evictions = identities.evictions, conflicts = identities.conflicts;
    identities.thread(pid, tid, qpc, start); identityChanges(evictions, conflicts);
}
void ProcessEventStore::traffic(ProcessTrafficObservation observation) {
    std::lock_guard lock(mutex);
    const auto epoch = transfers.info.attributionEpoch, dropped = transfers.info.pendingDropped, evicted = transfers.info.evictedSamples;
    transfers.observe(observation, identities);
    trafficChanges(epoch, dropped, evicted);
}
void ProcessEventStore::diskInitiated(ProcessDiskInitiation initiation) {
    std::lock_guard lock(mutex);
    const auto epoch = transfers.info.attributionEpoch, dropped = transfers.info.pendingDropped, evicted = transfers.info.evictedSamples;
    transfers.diskInitiated(initiation, identities);
    trafficChanges(epoch, dropped, evicted);
}
void ProcessEventStore::providers(SampleState disk, SampleState network, SampleState identity) {
    std::lock_guard lock(mutex); transfers.providers(disk, network, identity);
}
void ProcessEventStore::identityProvider(SampleState state) { std::lock_guard lock(mutex); transfers.info.identityProviderState = state; }
void ProcessEventStore::decodeFailure() { std::lock_guard lock(mutex); increment(info.decodeErrors); invalidate(false); }
void ProcessEventStore::lost(std::uint64_t events, std::uint64_t buffers) {
    std::lock_guard lock(mutex);
    if (events || buffers) { increment(info.providerEventsLost, events); increment(info.providerBuffersLost, buffers); invalidate(false); }
}
std::optional<std::uint64_t> ProcessEventStore::baselineWanted() {
    std::lock_guard lock(mutex);
    // Without payload CreateTime on every Stop/Start a snapshot generation could outlive its PID unnoticed.
    const auto identity = transfers.info.identityProviderState;
    if (info.boot.state != SampleState::valid || info.boot.id.empty() || (identity != SampleState::first && identity != SampleState::valid)
        || baselineEpoch == transfers.info.attributionEpoch) return std::nullopt;
    return transfers.info.attributionEpoch;
}
bool ProcessEventStore::baseline(std::uint64_t epoch, std::int64_t qpc, std::span<const ProcessBaselineEntry> entries) {
    std::lock_guard lock(mutex);
    const auto identity = transfers.info.identityProviderState;
    if (epoch != transfers.info.attributionEpoch || transfers.metadataIsLate(qpc) || qpc <= identities.resetFloor()
        || info.boot.state != SampleState::valid || (identity != SampleState::first && identity != SampleState::valid)) return false;
    const auto evictions = identities.evictions, conflicts = identities.conflicts;
    for (const auto& entry : entries) identities.baseline(entry.pid, entry.creationTime, qpc);
    identityChanges(evictions, conflicts);
    if (epoch != transfers.info.attributionEpoch) return false;
    baselineEpoch = epoch; return true;
}
void ProcessEventStore::acknowledgeResync(std::uint64_t version) {
    std::lock_guard lock(mutex);
    if (version == info.resyncVersion && version != std::numeric_limits<std::uint64_t>::max()) info.flags &= ~eventsResyncRequired;
}

ProcessEventBatch ProcessEventStore::read()
{
    std::lock_guard lock(mutex);
    ProcessEventBatch batch; batch.events.reserve((std::min)(records.size(), maximumFrameEvents));
    const auto epoch = transfers.info.attributionEpoch, dropped = transfers.info.pendingDropped, evicted = transfers.info.evictedSamples;
    batch.traffic = transfers.read(measurementTime(std::chrono::steady_clock::now()), identities);
    trafficChanges(epoch, dropped, evicted);
    batch.info = info;
    if (info.state == ProcessEventMonitorState::stopping && stopRequestedAt != std::chrono::steady_clock::time_point{}
        && std::chrono::steady_clock::now() - stopRequestedAt >= std::chrono::seconds(5)) {
        batch.info.state = ProcessEventMonitorState::stopTimedOut; batch.info.win32Error = ERROR_TIMEOUT;
    }
    while (!records.empty() && batch.events.size() < maximumFrameEvents) {
        batch.events.push_back(std::move(records.front())); records.pop_front();
    }
    return batch;
}

}
