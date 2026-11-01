#include "events_api.h"
#include "collectors/etw_collector.hpp"
#include <atomic>
#include <memory>
#include <new>

namespace {
std::atomic<unsigned> retainedFrames{};
constexpr unsigned maximumFrames = 8;
bool reserveFrame() {
    auto count = retainedFrames.load();
    while (count < maximumFrames) if (retainedFrames.compare_exchange_weak(count, count + 1)) return true;
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
    std::vector<NksProcessEvent> events;
    NksEventFrameView view{};
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
    }
};

static_assert(sizeof(NksProcessEvent) == 80 && sizeof(NksEventFrameView) == 112);

NksResult nks_events_create(NksEventMonitor** monitor) noexcept {
    if (!monitor) return NKS_INVALID_ARGUMENT;
    *monitor = nullptr;
    return invoke([&] { *monitor = new NksEventMonitor; return NKS_OK; });
}
NksResult nks_events_start(NksEventMonitor* monitor) noexcept {
    if (!monitor) return NKS_INVALID_ARGUMENT;
    return invoke([&] { monitor->collector.start(); return NKS_OK; });
}
void nks_events_stop(NksEventMonitor* monitor) noexcept { if (monitor) monitor->collector.stop(); }
void nks_events_release(NksEventMonitor* monitor) noexcept { delete monitor; }
NksResult nks_events_read(NksEventMonitor* monitor, NksEventFrame** frame) noexcept {
    if (!frame) return NKS_INVALID_ARGUMENT;
    *frame = nullptr;
    if (!monitor) return NKS_INVALID_ARGUMENT;
    if (!reserveFrame()) return NKS_NO_DATA;
    const auto result = invoke([&] {
        auto value = std::make_unique<NksEventFrame>();
        // Reserve before removing records, so allocation failure cannot silently lose a batch.
        value->events.reserve(nks::ProcessEventStore::maximumFrameEvents);
        value->batch = monitor->events->read(); value->build();
        *frame = value.release(); return NKS_OK;
    });
    if (result != NKS_OK) --retainedFrames;
    return result;
}
void nks_events_ack_resync(NksEventMonitor* monitor, std::uint64_t version) noexcept { if (monitor) monitor->events->acknowledgeResync(version); }
const NksEventFrameView* nks_events_frame_view(const NksEventFrame* frame) noexcept { return frame ? &frame->view : nullptr; }
void nks_events_frame_retain(NksEventFrame* frame) noexcept { if (frame) ++frame->references; }
void nks_events_frame_release(NksEventFrame* frame) noexcept {
    if (frame && frame->references.fetch_sub(1) == 1) { delete frame; --retainedFrames; }
}
