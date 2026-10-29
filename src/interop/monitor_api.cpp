#include "monitor_api.h"
#include "core/monitor.hpp"
#include <limits>
#include <stdexcept>

struct NksMonitor {
    nks::Monitor monitor;
    std::mutex readMutex;
    std::wstring processError, resourceError;
};

struct NksFrame {
    std::atomic<uint32_t> references{1};
    std::shared_ptr<const nks::ProcessSnapshot> processSnapshot;
    std::shared_ptr<const nks::ResourceSnapshot> resourceSnapshot;
    std::wstring processError, resourceError;
    std::vector<NksProcess> processes;
    std::vector<NksProcessor> processors;
    std::vector<NksResource> resources;
    NksFrameView view{};
    void build();
};

namespace {
template<class Action>
NksResult invoke(Action&& action) noexcept
{
    // Exceptions must not cross the C ABI.
    try { return action(); }
    catch (const std::invalid_argument&) { return NKS_INVALID_ARGUMENT; }
    catch (const std::bad_alloc&) { return NKS_OUT_OF_MEMORY; }
    catch (...) { return NKS_FAILED; }
}

uint32_t count(std::size_t size)
{
    if (size > std::numeric_limits<uint32_t>::max()) throw std::length_error("Snapshot count exceeds the ABI limit");
    return static_cast<uint32_t>(size);
}

NksString16 string(const std::wstring& value) { return {value.data(), count(value.size()), 0}; }
NksMetric metric(nks::Metric<double> value) { return {value.value, static_cast<uint32_t>(value.state), static_cast<uint32_t>(value.source)}; }
int64_t nanos(std::chrono::steady_clock::time_point value) { return std::chrono::duration_cast<std::chrono::nanoseconds>(value.time_since_epoch()).count(); }
}

static_assert(sizeof(wchar_t) == 2 && sizeof(void*) == 8);
static_assert(sizeof(NksString16) == 16 && sizeof(NksMetric) == 16);
static_assert(sizeof(NksProcess) == 152 && sizeof(NksProcessor) == 48);
static_assert(sizeof(NksMemory) == 96 && sizeof(NksProcessData) == 208);
static_assert(sizeof(NksResource) == 128 && sizeof(NksResourceData) == 32 && sizeof(NksFrameView) == 288);
static_assert(static_cast<unsigned>(nks::SampleState::valid) == NKS_SAMPLE_VALID && static_cast<unsigned>(nks::SampleState::gap) == NKS_SAMPLE_GAP);
static_assert(static_cast<unsigned>(nks::GenerationKind::creationTime) == NKS_GENERATION_CREATION_TIME && static_cast<unsigned>(nks::GenerationKind::sequenceNumber) == NKS_GENERATION_SEQUENCE_NUMBER);
static_assert(static_cast<unsigned>(nks::ResourceKind::cpu) == NKS_RESOURCE_CPU && static_cast<unsigned>(nks::ResourceKind::network) == NKS_RESOURCE_NETWORK);
static_assert(static_cast<unsigned>(nks::MetricSource::nativeProcess) == NKS_SOURCE_NATIVE_PROCESS && static_cast<unsigned>(nks::MetricSource::dxgi) == NKS_SOURCE_DXGI);

void NksFrame::build()
{
    view.abi_version = NKS_MONITOR_ABI_VERSION;
    view.read_at_ns = nks_monotonic_now_ns();
    view.process_error = string(processError);
    view.resource_error = string(resourceError);
    view.processes.processor_state = NKS_SAMPLE_UNAVAILABLE;
    view.processes.cpu_percent = {0, NKS_SAMPLE_UNAVAILABLE, NKS_SOURCE_PROCESSOR_TIME};
    view.processes.memory.state = view.processes.memory.page_file_state = NKS_SAMPLE_UNAVAILABLE;
    if (processSnapshot) {
        const auto& source = *processSnapshot;
        processes.reserve(source.processes.size());
        for (const auto& item : source.processes) {
            const auto& c = item.counters;
            processes.push_back({item.identity.bootTime, item.identity.pid, item.parentPid, item.identity.generation,
                static_cast<uint32_t>(item.identity.generationKind), c.threads, c.handles, 0,
                c.workingSetBytes, c.privateBytes, c.kernel100ns, c.user100ns, c.readIoBytes, c.writeIoBytes,
                metric(item.cpuPercent), metric(item.readIoBytesPerSecond), metric(item.writeIoBytesPerSecond), string(item.name)});
        }
        processors.reserve(source.processors.size());
        for (const auto& item : source.processors)
            processors.push_back({item.group, item.index, item.kernel100ns, item.user100ns, item.idle100ns, metric(item.cpuPercent)});
        const auto& m = source.memory;
        view.processes = {nanos(source.measuredAt), std::chrono::duration_cast<std::chrono::nanoseconds>(source.wallTime.time_since_epoch()).count(),
            source.collectionTime.count(), source.queryTime.count(), source.parseTime.count(), source.systemTime.count(),
            source.logicalProcessors, static_cast<uint32_t>(source.processorState), metric(source.systemCpuPercent), string(source.cpuModel),
            {m.physicalBytes, m.availableBytes, m.committedBytes, m.commitLimitBytes, m.systemCacheBytes, m.pagedPoolBytes, m.nonpagedPoolBytes,
             m.pageFileBytes, m.pageFileUsedBytes, m.pageFilePeakBytes, m.installedBytes, static_cast<uint32_t>(m.state), static_cast<uint32_t>(m.pageFileState)},
            processes.data(), count(processes.size()), count(processors.size()), processors.data()};
    }
    if (resourceSnapshot) {
        const auto& source = *resourceSnapshot;
        resources.reserve(source.devices.size());
        for (const auto& item : source.devices)
            resources.push_back({static_cast<uint32_t>(item.kind), 0, string(item.id), string(item.title), string(item.description), string(item.status),
                metric(item.primary), metric(item.secondary), metric(item.tertiary), item.capacity});
        view.resources = {nanos(source.measuredAt), source.diskCountersAvailable ? 1u : 0u, source.gpuCountersAvailable ? 1u : 0u,
            resources.data(), count(resources.size()), 0};
    }
}

NksResult __cdecl nks_monitor_create(uint32_t abiVersion, uint32_t interval, int32_t paused, NksMonitor** result) noexcept
{
    if (!result) return NKS_INVALID_ARGUMENT;
    *result = nullptr;
    if (abiVersion != NKS_MONITOR_ABI_VERSION) return NKS_ABI_MISMATCH;
    if (paused != 0 && paused != 1) return NKS_INVALID_ARGUMENT;
    return invoke([&] {
        auto instance = std::make_unique<NksMonitor>();
        instance->monitor.setInterval(interval);
        instance->monitor.setPaused(paused != 0);
        instance->monitor.start();
        *result = instance.release();
        return NKS_OK;
    });
}

void __cdecl nks_monitor_destroy(NksMonitor* monitor) noexcept { delete monitor; }

NksResult __cdecl nks_monitor_set_paused(NksMonitor* monitor, int32_t paused) noexcept
{
    if (!monitor || (paused != 0 && paused != 1)) return NKS_INVALID_ARGUMENT;
    monitor->monitor.setPaused(paused != 0);
    return NKS_OK;
}

NksResult __cdecl nks_monitor_set_interval(NksMonitor* monitor, uint32_t interval) noexcept
{
    if (!monitor) return NKS_INVALID_ARGUMENT;
    return invoke([&] { monitor->monitor.setInterval(interval); return NKS_OK; });
}

NksResult __cdecl nks_monitor_read(NksMonitor* monitor, NksFrame** result) noexcept
{
    if (!result) return NKS_INVALID_ARGUMENT;
    *result = nullptr;
    if (!monitor) return NKS_INVALID_ARGUMENT;
    return invoke([&] {
        std::lock_guard lock(monitor->readMutex);
        auto frame = std::make_unique<NksFrame>();
        frame->processSnapshot = monitor->monitor.takeProcesses(frame->processError);
        frame->resourceSnapshot = monitor->monitor.takeResources(frame->resourceError);
        if (frame->processSnapshot) frame->view.flags |= NKS_PROCESS_DATA;
        if (frame->resourceSnapshot) frame->view.flags |= NKS_RESOURCE_DATA;
        if (frame->processError != monitor->processError) frame->view.flags |= NKS_PROCESS_ERROR_CHANGED;
        if (frame->resourceError != monitor->resourceError) frame->view.flags |= NKS_RESOURCE_ERROR_CHANGED;
        if (!frame->view.flags) return NKS_NO_DATA;
        frame->build();
        auto processError = frame->processError, resourceError = frame->resourceError;
        monitor->processError.swap(processError);
        monitor->resourceError.swap(resourceError);
        *result = frame.release();
        return NKS_OK;
    });
}

NksResult __cdecl nks_frame_get_view(const NksFrame* frame, uint32_t size, NksFrameView* result) noexcept
{
    if (!frame || !result) return NKS_INVALID_ARGUMENT;
    if (size != sizeof(NksFrameView)) return NKS_ABI_MISMATCH;
    *result = frame->view;
    return NKS_OK;
}

void __cdecl nks_frame_retain(NksFrame* frame) noexcept { if (frame) frame->references.fetch_add(1, std::memory_order_relaxed); }
void __cdecl nks_frame_release(NksFrame* frame) noexcept
{
    if (frame && frame->references.fetch_sub(1, std::memory_order_acq_rel) == 1) delete frame;
}
int64_t __cdecl nks_monotonic_now_ns() noexcept { return nanos(std::chrono::steady_clock::now()); }
