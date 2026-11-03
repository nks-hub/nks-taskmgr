#include "../src/interop/events_api.cpp"
#include <chrono>
#include <utility>

namespace {
using MonitorOwner = std::unique_ptr<NksEventMonitor, decltype(&nks_events_release)>;
using FrameOwner = std::unique_ptr<NksEventFrame, decltype(&nks_events_frame_release)>;

NksResult seededTraffic(MonitorOwner& monitor)
{
    NksEventMonitor* value = nullptr;
    const auto result = nks_events_create(&value);
    monitor.reset(value);
    if (result != NKS_OK) return result;
    auto& store = *monitor->events;
    nks::BootIdentity boot; boot.id = {1, 2}; boot.state = nks::SampleState::valid;
    store.initialize(boot, 1000);
    store.providers(nks::SampleState::gap, nks::SampleState::gap, nks::SampleState::valid);
    const auto observed = nks::measurementTime(std::chrono::steady_clock::now());
    nks::ProcessEventRecord process;
    process.kind = nks::ProcessEventKind::started; process.pid = 42;
    process.identityKind = nks::GenerationKind::creationTime; process.generation = 9000;
    process.timestampQpc = 10; process.observedAtNs = observed;
    store.push(std::move(process)); store.thread(42, 7, 20, true);
    store.diskInitiated({30, observed, 1000, 7, nks::ProcessTrafficDirection::read});
    store.traffic({60, observed, 7, 4096, nks::ProcessTrafficSource::physicalDisk,
        nks::ProcessTrafficDirection::read, false, 1000});
    store.state(nks::ProcessEventMonitorState::stopped);
    return NKS_OK;
}

NksResult readTraffic(NksEventMonitor* monitor, FrameOwner& frame)
{
    NksEventFrame* value = nullptr;
    const auto result = nks_events_read(monitor, &value);
    frame.reset(value); return result;
}

bool issuingFrame(const NksEventFrame& frame)
{
    if (frame.batch.traffic.samples.size() != 1 || frame.traffic.size() != 1 || frame.trafficView.sample_count != 1
        || frame.trafficView.samples != frame.traffic.data()) return false;
    const auto& core = frame.batch.traffic.samples.front();
    const auto& row = frame.traffic.front();
    return core.attribution == nks::ProcessTrafficAttribution::issuingThreadOwner && core.identityState == nks::SampleState::valid
        && core.pid == 42 && core.generation == 9000 && core.operations == 1 && core.observedBytes == 4096
        && row.attribution == NKS_TRAFFIC_ISSUING_THREAD_OWNER && row.pid == 42 && row.generation == 9000
        && row.generation_kind == NKS_GENERATION_CREATION_TIME && row.identity_state == NKS_SAMPLE_VALID
        && row.source == NKS_TRAFFIC_PHYSICAL_DISK && row.direction == NKS_TRAFFIC_READ
        && row.state == NKS_SAMPLE_GAP && (row.flags & NKS_TRAFFIC_PARTIAL) && row.observed_bytes == 4096 && row.operations == 1
        && row.sample_id == core.sampleId && row.attribution_epoch == core.attributionEpoch
        && frame.view.boot_low == 1 && frame.view.boot_high == 2 && frame.view.boot_state == NKS_SAMPLE_VALID;
}

bool revocationFrame(const NksEventFrame& before, const NksEventFrame& after)
{
    if (after.batch.traffic.samples.size() != 1 || after.traffic.size() != 1 || after.trafficView.sample_count != 1
        || after.trafficView.samples != after.traffic.data()) return false;
    const auto& core = after.batch.traffic.samples.front();
    const auto& original = before.traffic.front();
    const auto& row = after.traffic.front();
    return core.attribution == nks::ProcessTrafficAttribution::unknown && core.identityState == nks::SampleState::unavailable
        && row.sample_id == original.sample_id && row.attribution_epoch == original.attribution_epoch
        && row.attribution == NKS_TRAFFIC_UNKNOWN && row.identity_state == NKS_SAMPLE_UNAVAILABLE
        && row.generation_kind == NKS_GENERATION_MISSING && row.generation == 0 && row.pid == 0
        && row.state == NKS_SAMPLE_GAP && (row.flags & NKS_TRAFFIC_PARTIAL)
        && row.observed_bytes == original.observed_bytes && row.operations == original.operations
        && after.trafficView.attribution_epoch != before.trafficView.attribution_epoch;
}
}

extern "C" NKS_MONITOR_API NksResult nks_test_traffic_frame(std::uint32_t variant, NksEventFrame** output) noexcept
{
    if (!output) return NKS_INVALID_ARGUMENT;
    *output = nullptr;
    if (variant > 10) return NKS_INVALID_ARGUMENT;
    return invoke([&] {
        MonitorOwner monitor{nullptr, nks_events_release};
        auto result = seededTraffic(monitor);
        if (result != NKS_OK) return result;
        FrameOwner frame{nullptr, nks_events_frame_release};
        result = readTraffic(monitor.get(), frame);
        if (result != NKS_OK) return result;
        if (!frame || !issuingFrame(*frame)) return NKS_FAILED;
        // Only the serialized row is corrupted; core attribution and API frame ownership remain real.
        auto& row = frame->traffic.front();
        switch (variant) {
        case 0: break;
        case 1: row.source = NKS_TRAFFIC_TCP; row.direction = NKS_TRAFFIC_SENT; break;
        case 2: row.generation = 0; break;
        case 3: row.generation_kind = NKS_GENERATION_SEQUENCE_NUMBER; break;
        case 4: row.identity_state = NKS_SAMPLE_UNAVAILABLE; break;
        case 5: row.state = NKS_SAMPLE_VALID; break;
        case 6: row.flags = 0; break;
        case 7: frame->view.boot_low = frame->view.boot_high = 0; break;
        case 8: row.attribution = 4; break;
        case 9: row.flags = NKS_TRAFFIC_PARTIAL | NKS_TRAFFIC_IPV6; break;
        case 10: row.flags = NKS_TRAFFIC_PARTIAL | 128u; break;
        }
        *output = frame.release(); return NKS_OK;
    });
}

extern "C" NKS_MONITOR_API NksResult nks_test_traffic_pair(NksEventFrame** before, NksEventFrame** after) noexcept
{
    if (before) *before = nullptr;
    if (after) *after = nullptr;
    if (!before || !after || before == after) return NKS_INVALID_ARGUMENT;
    return invoke([&] {
        MonitorOwner monitor{nullptr, nks_events_release};
        auto result = seededTraffic(monitor);
        if (result != NKS_OK) return result;
        FrameOwner first{nullptr, nks_events_frame_release}, second{nullptr, nks_events_frame_release};
        result = readTraffic(monitor.get(), first);
        if (result != NKS_OK) return result;
        if (!first || !issuingFrame(*first)) return NKS_FAILED;
        monitor->events->lost(1, 0);
        result = readTraffic(monitor.get(), second);
        if (result != NKS_OK) return result;
        if (!second || !revocationFrame(*first, *second)) return NKS_FAILED;
        *before = first.release(); *after = second.release(); return NKS_OK;
    });
}

extern "C" NKS_MONITOR_API NksResult nks_test_traffic_counts(std::uint32_t* frames, std::uint32_t* monitors, std::uint64_t* budget) noexcept
{
    if (!frames || !monitors || !budget) return NKS_INVALID_ARGUMENT;
    *frames = retainedFrames.load(); *monitors = retainedMonitors.load(); *budget = nks::eventBudgetUsed.load();
    return NKS_OK;
}
