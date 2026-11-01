#include "events_api.h"
#include "collectors/etw_collector.hpp"
#include <atomic>
#include <memory>
#include <new>

namespace {
std::atomic<unsigned> retainedFrames{};
std::atomic<unsigned> retainedMonitors{};
constexpr unsigned maximumFrames = 8;
bool reserve(std::atomic<unsigned>& counter) {
    auto count = counter.load();
    while (count < maximumFrames) if (counter.compare_exchange_weak(count, count + 1)) return true;
    return false;
}
template<class Action> NksResult invoke(Action&& action) noexcept {
    try { return action(); }
    catch (const std::bad_alloc&) { return NKS_OUT_OF_MEMORY; }
    catch (...) { return NKS_FAILED; }
}
}

struct NksEventMonitor {
    std::shared_ptr<nks::ProcessEventStore> events = std::make_shared<nks::ProcessEventStore>();
    nks::EtwCollector collector{events};
};

struct NksEventFrame {
    std::atomic<std::uint32_t> references{1};
    nks::ProcessEventBatch batch;
    nks::EventVector<NksProcessEvent> events;
    nks::EventVector<NksProcessTrafficSample> traffic;
    NksEventFrameView view{};
    NksProcessTrafficView trafficView{};
    void build() {
        const auto& source = batch.info;
        events.reserve(batch.events.size());
        for (const auto& record : batch.events) {
            events.push_back({record.sequence, record.timestampQpc, record.observedAtNs, static_cast<std::uint32_t>(record.kind),
                record.pid, record.parentPid, static_cast<std::uint32_t>(record.identityState), static_cast<std::uint32_t>(record.identityKind),
                record.flags, record.generation, record.exitStatus, 0, {record.imageName.data(), static_cast<std::uint32_t>(record.imageName.size()), 0}});
        }
        view = {1, static_cast<std::uint32_t>(source.state), source.win32Error, source.flags,
            source.boot.id.low, source.boot.id.high, static_cast<std::uint32_t>(source.boot.state), 0, source.qpcFrequency,
            source.providerEventsLost, source.providerBuffersLost, source.queueDropped, source.decodeErrors,
            source.ambiguous, source.resyncVersion, events.data(), static_cast<std::uint32_t>(events.size()), 0};
        for (const auto& sample : batch.traffic.samples) {
            const auto& m = sample.measurement;
            traffic.push_back({sample.sampleId, sample.startQpc, sample.endQpc, sample.observedAtNs, sample.pid,
                static_cast<std::uint32_t>(sample.identityKind), sample.generation, static_cast<std::uint32_t>(sample.identityState),
                static_cast<std::uint32_t>(sample.attribution), static_cast<std::uint32_t>(sample.source), static_cast<std::uint32_t>(sample.direction),
                sample.observedBytes, sample.operations, sample.observedBytesPerSecond, static_cast<std::uint32_t>(sample.state), sample.flags,
                sample.attributionEpoch, {static_cast<std::uint8_t>(m.unit), static_cast<std::uint8_t>(m.normalization),
                    static_cast<std::uint8_t>(m.basis), static_cast<std::uint8_t>(m.intervalState), m.normalizationDivisor,
                    m.observedAtNs, m.intervalNs, m.cadenceNs}});
        }
        const auto& t = batch.traffic.info;
        trafficView = {1, static_cast<std::uint32_t>(traffic.size()), traffic.data(), static_cast<std::uint32_t>(t.diskState),
            static_cast<std::uint32_t>(t.networkState), static_cast<std::uint32_t>(t.identityProviderState), t.flags, t.observedAtNs,
            t.attributionEpoch, t.unknownEvents, t.unknownBytes, t.evictedSamples, t.pendingDropped, t.lateEvents,
            t.identityConflicts, nks::eventBudgetUsed.load(), t.budgetLimit};
    }
};

static_assert(sizeof(NksProcessEvent) == 80 && sizeof(NksEventFrameView) == 112);
static_assert(sizeof(NksProcessTrafficSample) == 136 && sizeof(NksProcessTrafficView) == 112);

NksResult nks_events_create(NksEventMonitor** monitor) noexcept {
    if (!monitor) return NKS_INVALID_ARGUMENT;
    *monitor = nullptr;
    if (!reserve(retainedMonitors)) return NKS_NO_DATA;
    const auto result = invoke([&] { *monitor = new NksEventMonitor; return NKS_OK; });
    if (result != NKS_OK) --retainedMonitors;
    return result;
}
NksResult nks_events_start(NksEventMonitor* monitor) noexcept {
    if (!monitor) return NKS_INVALID_ARGUMENT;
    return invoke([&] { monitor->collector.start(); return NKS_OK; });
}
void nks_events_stop(NksEventMonitor* monitor) noexcept { if (monitor) monitor->collector.stop(); }
void nks_events_release(NksEventMonitor* monitor) noexcept { if (monitor) { delete monitor; --retainedMonitors; } }
NksResult nks_events_read(NksEventMonitor* monitor, NksEventFrame** frame) noexcept {
    if (!frame) return NKS_INVALID_ARGUMENT;
    *frame = nullptr;
    if (!monitor) return NKS_INVALID_ARGUMENT;
    if (!reserve(retainedFrames)) return NKS_NO_DATA;
    const auto result = invoke([&] {
        auto value = std::make_unique<NksEventFrame>();
        // Reserve before removing records, so allocation failure cannot silently lose a batch.
        value->events.reserve(nks::ProcessEventStore::maximumFrameEvents);
        value->traffic.reserve(nks::ProcessTrafficStore::maximumFrameSamples);
        value->batch = monitor->events->read(); value->build();
        *frame = value.release(); return NKS_OK;
    });
    if (result != NKS_OK) --retainedFrames;
    return result;
}
void nks_events_ack_resync(NksEventMonitor* monitor, std::uint64_t version) noexcept { if (monitor) monitor->events->acknowledgeResync(version); }
const NksEventFrameView* nks_events_frame_view(const NksEventFrame* frame) noexcept { return frame ? &frame->view : nullptr; }
NksResult nks_events_frame_get_traffic(const NksEventFrame* frame, std::uint32_t viewSize, NksProcessTrafficView* result) noexcept {
    if (!frame || !result) return NKS_INVALID_ARGUMENT;
    if (viewSize != sizeof(NksProcessTrafficView)) return NKS_ABI_MISMATCH;
    *result = frame->trafficView; return NKS_OK;
}
void nks_events_frame_retain(NksEventFrame* frame) noexcept { if (frame) ++frame->references; }
void nks_events_frame_release(NksEventFrame* frame) noexcept {
    if (frame && frame->references.fetch_sub(1) == 1) { delete frame; --retainedFrames; }
}
