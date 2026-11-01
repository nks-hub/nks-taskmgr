#include "process_events.hpp"
#include <windows.h>
#include <algorithm>
#include <limits>

namespace nks {
namespace {
void increment(std::uint64_t& value, std::uint64_t amount = 1) {
    value += (std::min)(amount, std::numeric_limits<std::uint64_t>::max() - value);
}
}

void ProcessEventStore::resync()
{
    info.flags |= eventsResyncRequired; increment(info.resyncVersion);
}

void ProcessEventStore::initialize(BootIdentity boot, std::int64_t qpcFrequency)
{
    std::lock_guard lock(mutex);
    info.boot = boot; info.qpcFrequency = qpcFrequency;
    resync();
}

void ProcessEventStore::state(ProcessEventMonitorState value, std::uint32_t error, bool cleanupOutstanding)
{
    std::lock_guard lock(mutex);
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
    // ETW kernel object pointers and current PID lookups are never lifetime identities.
    record.identityKind = GenerationKind::missing; record.generation = 0; record.identityState = SampleState::unavailable;
    if (record.imageName.size() > maximumNameChars) {
        record.imageName.clear(); record.flags = (record.flags & ~eventImageName) | eventPartial;
        increment(info.decodeErrors); resync();
    }
    records.push_back(std::move(record)); increment(info.ambiguous);
}

void ProcessEventStore::decodeFailure() { std::lock_guard lock(mutex); increment(info.decodeErrors); resync(); }
void ProcessEventStore::lost(std::uint64_t events, std::uint64_t buffers) {
    std::lock_guard lock(mutex);
    if (events || buffers) { increment(info.providerEventsLost, events); increment(info.providerBuffersLost, buffers); resync(); }
}
void ProcessEventStore::acknowledgeResync(std::uint64_t version) {
    std::lock_guard lock(mutex);
    if (version == info.resyncVersion && version != std::numeric_limits<std::uint64_t>::max()) info.flags &= ~eventsResyncRequired;
}

ProcessEventBatch ProcessEventStore::read()
{
    std::lock_guard lock(mutex);
    ProcessEventBatch batch; batch.events.reserve((std::min)(records.size(), maximumFrameEvents)); batch.info = info;
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
