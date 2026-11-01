#include "history_api.h"
#include "monitor_handle.hpp"
#include <atomic>
#include <memory>

struct NksHistoryFrame {
    std::atomic<unsigned> references{1};
    std::unique_ptr<nks::HistoryBatch> batch;
    std::vector<NksHistoryDefinition> definitions;
    std::vector<NksHistoryPoint> points;
    NksHistoryView view{};

    void build() {
        const auto text = [](const std::wstring& value) {
            return NksString16{value.data(), static_cast<std::uint32_t>(value.size()), 0};
        };
        definitions.reserve(batch->definitions.size());
        for (const auto& definition : batch->definitions)
            definitions.push_back({definition.id, static_cast<std::uint32_t>(definition.channel), definition.group, definition.index,
                text(definition.resourceId), text(definition.detailId), text(definition.name)});
        points.reserve(batch->points.size());
        for (const auto& point : batch->points)
            points.push_back({point.sequence, point.timeNs, point.series, 0,
                {point.metric.value, static_cast<std::uint32_t>(point.metric.state), static_cast<std::uint32_t>(point.metric.source)}});
        view = {NKS_HISTORY_ABI_VERSION, 0, batch->lostPoints, batch->captureErrors,
            definitions.data(), static_cast<std::uint32_t>(definitions.size()), 0,
            points.data(), static_cast<std::uint32_t>(points.size()), batch->morePending ? 1u : 0u};
    }
};

static_assert(sizeof(NksHistoryDefinition) == 64 && sizeof(NksHistoryPoint) == 40 && sizeof(NksHistoryView) == 56);

extern "C" NksResult __cdecl nks_monitor_read_history(NksMonitor* monitor, NksHistoryFrame** result) noexcept
{
    if (!result) return NKS_INVALID_ARGUMENT;
    *result = nullptr;
    if (!monitor) return NKS_INVALID_ARGUMENT;
    try {
        auto frame = std::make_unique<NksHistoryFrame>();
        frame->batch = monitor->monitor.takeHistory();
        if (!frame->batch) return NKS_NO_DATA;
        frame->build();
        *result = frame.release();
        return NKS_OK;
    } catch (const std::bad_alloc&) { monitor->monitor.historyDeliveryFailed(); return NKS_OUT_OF_MEMORY; }
    catch (...) { monitor->monitor.historyDeliveryFailed(); return NKS_FAILED; }
}

extern "C" NksResult __cdecl nks_history_frame_get_view(const NksHistoryFrame* frame, uint32_t size, NksHistoryView* result) noexcept
{
    if (!result) return NKS_INVALID_ARGUMENT;
    if (size != sizeof(NksHistoryView)) return NKS_ABI_MISMATCH;
    *result = {};
    if (!frame) return NKS_INVALID_ARGUMENT;
    *result = frame->view;
    return NKS_OK;
}

extern "C" void __cdecl nks_history_frame_retain(NksHistoryFrame* frame) noexcept
{
    if (frame) frame->references.fetch_add(1, std::memory_order_relaxed);
}

extern "C" void __cdecl nks_history_frame_release(NksHistoryFrame* frame) noexcept
{
    if (frame && frame->references.fetch_sub(1, std::memory_order_acq_rel) == 1) delete frame;
}
