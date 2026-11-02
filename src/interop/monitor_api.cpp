#include "monitor_api.h"
#include "measurement_api.h"
#include "process_changes_api.h"
#include "core/process_delta.hpp"
#include "monitor_handle.hpp"
#include "collectors/process_identity.hpp"
#include <limits>
#include <stdexcept>

struct NksFrame {
    std::atomic<uint32_t> references{1};
    std::shared_ptr<const nks::ProcessSnapshot> processSnapshot;
    std::shared_ptr<const nks::ResourceSnapshot> resourceSnapshot;
    std::wstring processError, resourceError;
    std::vector<NksProcess> processes;
    std::vector<NksProcessCreation> processCreation;
    std::vector<NksProcessor> processors;
    std::vector<NksResource> resources;
    std::vector<NksGpu> gpus;
    std::vector<std::vector<NksGpuEngine>> gpuEngines;
    std::vector<NksHardwareFact> hardware;
    NksProcessMeasurements processMeasurements{};
    std::vector<NksResourceMeasurements> resourceMeasurements;
    std::vector<NksProcessChange> processChanges;
    std::uint64_t processRevision{}, processBaseRevision{};
    std::uint32_t processChangeFlags{};
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
NksMeasurementMetadata measurement(const nks::MeasurementMetadata& value)
{
    return {static_cast<uint8_t>(value.unit), static_cast<uint8_t>(value.normalization), static_cast<uint8_t>(value.basis),
        static_cast<uint8_t>(value.intervalState), value.normalizationDivisor, value.observedAtNs, value.intervalNs, value.cadenceNs};
}
}

static_assert(sizeof(wchar_t) == 2 && sizeof(void*) == 8);
static_assert(sizeof(NksString16) == 16 && sizeof(NksMetric) == 16);
static_assert(sizeof(NksProcess) == 152 && sizeof(NksProcessor) == 48);
static_assert(sizeof(NksMemory) == 96 && sizeof(NksProcessData) == 208);
static_assert(sizeof(NksResource) == 128 && sizeof(NksResourceData) == 32 && sizeof(NksFrameView) == 288);
static_assert(sizeof(NksGpuEngine) == 48 && sizeof(NksGpu) == 56 && sizeof(NksGpuData) == 16);
static_assert(sizeof(NksHardwareFact) == 48 && sizeof(NksHardwareData) == 16);
static_assert(sizeof(NksProcessCreation) == 32 && sizeof(NksProcessCreationData) == 16 && sizeof(NksProcessIdentityData) == 40);
static_assert(sizeof(NksMeasurementMetadata) == 32 && offsetof(NksMeasurementMetadata, observed_at_ns) == 8);
static_assert(sizeof(NksProcessChange) == 24 && sizeof(NksProcessChangesData) == 40);
static_assert(sizeof(NksProcessMeasurements) == 192 && sizeof(NksResourceMeasurements) == 240 && sizeof(NksMeasurementData) == 216);
static_assert(static_cast<unsigned>(nks::MeasurementUnit::operationsPerSecond) == NKS_UNIT_OPERATIONS_PER_SECOND);
static_assert(static_cast<unsigned>(nks::MeasurementNormalization::deviceActiveTime) == NKS_NORMALIZATION_DEVICE_ACTIVE_TIME);
static_assert(static_cast<unsigned>(nks::MeasurementBasis::providerDefined) == NKS_BASIS_PROVIDER_DEFINED);
static_assert(static_cast<unsigned>(nks::MeasurementIntervalState::unavailable) == NKS_INTERVAL_UNAVAILABLE);
static_assert(static_cast<unsigned>(nks::SampleState::valid) == NKS_SAMPLE_VALID && static_cast<unsigned>(nks::SampleState::gap) == NKS_SAMPLE_GAP);
static_assert(static_cast<unsigned>(nks::GenerationKind::creationTime) == NKS_GENERATION_CREATION_TIME && static_cast<unsigned>(nks::GenerationKind::sequenceNumber) == NKS_GENERATION_SEQUENCE_NUMBER);
static_assert(static_cast<unsigned>(nks::ResourceKind::cpu) == NKS_RESOURCE_CPU && static_cast<unsigned>(nks::ResourceKind::network) == NKS_RESOURCE_NETWORK);
static_assert(static_cast<unsigned>(nks::MetricSource::nativeProcess) == NKS_SOURCE_NATIVE_PROCESS && static_cast<unsigned>(nks::MetricSource::dxgi) == NKS_SOURCE_DXGI);

void NksFrame::build()
{
    const auto facts = [&](const std::wstring& id, const std::vector<nks::HardwareFact>& values) {
        for (const auto& fact : values) hardware.push_back({string(id), string(fact.name), string(fact.value)});
    };
    static const std::wstring memoryId = L"memory", cpuId = L"cpu", boardId = L"board", upsId = L"ups", cpuSensorsId = L"cpu-sensors";
    view.abi_version = NKS_MONITOR_ABI_VERSION;
    view.read_at_ns = nks_monotonic_now_ns();
    view.process_error = string(processError);
    view.resource_error = string(resourceError);
    view.processes.processor_state = NKS_SAMPLE_UNAVAILABLE;
    view.processes.cpu_percent = {0, NKS_SAMPLE_UNAVAILABLE, NKS_SOURCE_PROCESSOR_TIME};
    view.processes.memory.state = view.processes.memory.page_file_state = NKS_SAMPLE_UNAVAILABLE;
    if (processSnapshot) {
        const auto& source = *processSnapshot;
        const auto& measured = source.measurements;
        processMeasurements = {measurement(measured.processCpu), measurement(measured.processIo), measurement(measured.processMemory),
            measurement(measured.systemCpu), measurement(measured.logicalCpu), measurement(measured.systemMemory)};
        facts(memoryId, source.memoryFacts); facts(cpuId, source.cpuFacts); facts(boardId, source.boardFacts);
        processes.reserve(source.processes.size());
        processCreation.reserve(source.processes.size());
        for (const auto& item : source.processes) {
            const auto& c = item.counters;
            processes.push_back({source.bootTime, item.identity.pid, item.parentPid, item.identity.generation,
                static_cast<uint32_t>(item.identity.generationKind), c.threads, c.handles, 0,
                c.workingSetBytes, c.privateBytes, c.kernel100ns, c.user100ns, c.readIoBytes, c.writeIoBytes,
                metric(item.cpuPercent), metric(item.readIoBytesPerSecond), metric(item.writeIoBytesPerSecond), string(item.name)});
            processCreation.push_back({item.identity.pid, static_cast<uint32_t>(item.identity.generationKind), item.identity.generation,
                item.created100ns, static_cast<uint32_t>(item.created100ns ? NKS_SAMPLE_VALID : NKS_SAMPLE_UNAVAILABLE), 0});
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
        resourceMeasurements.reserve(source.devices.size());
        for (const auto& item : source.devices) {
            const auto& measured = item.measurements;
            resourceMeasurements.push_back({string(item.id), measurement(measured.primary), measurement(measured.secondary), measurement(measured.tertiary),
                measurement(measured.temperature), measurement(measured.capacity), measurement(measured.sharedCapacity), measurement(measured.engineUtilization)});
        }
        facts(upsId, source.upsFacts);
        facts(cpuSensorsId, source.cpuSensorFacts);
        resources.reserve(source.devices.size());
        gpuEngines.reserve(source.devices.size());
        for (const auto& item : source.devices) {
            facts(item.id, item.facts);
            resources.push_back({static_cast<uint32_t>(item.kind), 0, string(item.id), string(item.title), string(item.description), string(item.status),
                metric(item.primary), metric(item.secondary), metric(item.tertiary), item.capacity});
            if (item.kind == nks::ResourceKind::gpu) {
                auto& engines = gpuEngines.emplace_back();
                for (const auto& engine : item.engines) engines.push_back({string(engine.id), string(engine.name), metric(engine.utilization)});
                gpus.push_back({string(item.id), item.sharedCapacity, metric(item.temperature), engines.data(), count(engines.size()), 0});
            }
        }
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

NksResult __cdecl nks_monitor_set_cpu_sensors(NksMonitor* monitor, uint32_t enabled) noexcept
{
    if (!monitor || enabled > 1) return NKS_INVALID_ARGUMENT;
    monitor->monitor.setCpuSensors(enabled != 0);
    return NKS_OK;
}

NksResult __cdecl nks_monitor_read(NksMonitor* monitor, NksFrame** result) noexcept
{
    if (!result) return NKS_INVALID_ARGUMENT;
    *result = nullptr;
    if (!monitor) return NKS_INVALID_ARGUMENT;
    return invoke([&] {
        std::lock_guard lock(monitor->readMutex);
        std::wstring pendingProcessError, pendingResourceError;
        auto processSnapshot = monitor->monitor.takeProcesses(pendingProcessError);
        auto resourceSnapshot = monitor->monitor.takeResources(pendingResourceError);
        uint32_t flags = 0;
        if (processSnapshot) flags |= NKS_PROCESS_DATA;
        if (resourceSnapshot) flags |= NKS_RESOURCE_DATA;
        if (pendingProcessError != monitor->processError) flags |= NKS_PROCESS_ERROR_CHANGED;
        if (pendingResourceError != monitor->resourceError) flags |= NKS_RESOURCE_ERROR_CHANGED;
        if (!flags) return NKS_NO_DATA;
        auto frame = std::make_unique<NksFrame>();
        frame->processSnapshot = std::move(processSnapshot);
        frame->resourceSnapshot = std::move(resourceSnapshot);
        frame->processError = std::move(pendingProcessError);
        frame->resourceError = std::move(pendingResourceError);
        frame->view.flags = flags;
        frame->build();
        if (frame->processSnapshot) {
            const auto changes = nks::processChanges(monitor->previousProcesses.get(), *frame->processSnapshot);
            frame->processBaseRevision = monitor->processRevision;
            frame->processRevision = monitor->processRevision + 1;
            frame->processChangeFlags = changes.reset ? NKS_PROCESS_CHANGES_RESET : 0;
            if (!frame->processRevision) { frame->processRevision = 1; frame->processChangeFlags |= NKS_PROCESS_CHANGES_RESET; }
            frame->processChanges.reserve(changes.entries.size());
            for (const auto& entry : changes.entries) frame->processChanges.push_back({static_cast<uint32_t>(entry.kind), entry.index,
                entry.pid, static_cast<uint32_t>(entry.generationKind), entry.generation});
        }
        auto processError = frame->processError, resourceError = frame->resourceError;
        monitor->processError.swap(processError);
        monitor->resourceError.swap(resourceError);
        if (frame->processSnapshot) {
            monitor->previousProcesses = frame->processSnapshot;
            monitor->processRevision = frame->processRevision;
        }
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

NksResult __cdecl nks_frame_get_measurement_data(const NksFrame* frame, uint32_t size, NksMeasurementData* result) noexcept
{
    if (!frame || !result) return NKS_INVALID_ARGUMENT;
    if (size != sizeof(NksMeasurementData)) return NKS_ABI_MISMATCH;
    *result = {NKS_MEASUREMENT_ABI_VERSION, frame->view.flags & (NKS_PROCESS_DATA | NKS_RESOURCE_DATA), frame->processMeasurements,
        frame->resourceMeasurements.data(), count(frame->resourceMeasurements.size()), 0};
    return NKS_OK;
}

NksResult __cdecl nks_frame_get_process_changes(const NksFrame* frame, uint32_t size, NksProcessChangesData* result) noexcept
{
    if (!frame || !result) return NKS_INVALID_ARGUMENT;
    if (size != sizeof(NksProcessChangesData)) return NKS_ABI_MISMATCH;
    if (!frame->processSnapshot) return NKS_NO_DATA;
    *result = {NKS_PROCESS_CHANGES_ABI_VERSION, frame->processChangeFlags, frame->processRevision, frame->processBaseRevision,
        frame->processChanges.data(), count(frame->processChanges.size()), count(frame->processes.size())};
    return NKS_OK;
}

void __cdecl nks_frame_retain(NksFrame* frame) noexcept { if (frame) frame->references.fetch_add(1, std::memory_order_relaxed); }
NksResult __cdecl nks_frame_get_gpu_data(const NksFrame* frame, uint32_t size, NksGpuData* result) noexcept
{
    if (!frame || !result) return NKS_INVALID_ARGUMENT;
    if (size != sizeof(NksGpuData)) return NKS_ABI_MISMATCH;
    *result = {frame->gpus.data(), count(frame->gpus.size()), 0};
    return NKS_OK;
}
NksResult __cdecl nks_frame_get_hardware_data(const NksFrame* frame, uint32_t size, NksHardwareData* result) noexcept
{
    if (!frame || !result) return NKS_INVALID_ARGUMENT;
    if (size != sizeof(NksHardwareData)) return NKS_ABI_MISMATCH;
    *result = {frame->hardware.data(), count(frame->hardware.size()), 0};
    return NKS_OK;
}
NksResult __cdecl nks_frame_get_boot_data(const NksFrame* frame, uint32_t size, NksBootData* result) noexcept
{
    if (!frame || !result) return NKS_INVALID_ARGUMENT;
    if (size != sizeof(NksBootData)) return NKS_ABI_MISMATCH;
    *result = {0, 0, NKS_SAMPLE_UNAVAILABLE, 0};
    if (!frame->processSnapshot) return NKS_NO_DATA;
    const auto& boot = frame->processSnapshot->boot;
    *result = {boot.id.low, boot.id.high, static_cast<uint32_t>(boot.state), 0};
    return NKS_OK;
}
void __cdecl nks_frame_release(NksFrame* frame) noexcept
{
    if (frame && frame->references.fetch_sub(1, std::memory_order_acq_rel) == 1) delete frame;
}
NksResult __cdecl nks_frame_get_process_creation_data(const NksFrame* frame, uint32_t size, NksProcessCreationData* result) noexcept
{
    if (!frame || !result) return NKS_INVALID_ARGUMENT;
    if (size != sizeof(NksProcessCreationData)) return NKS_ABI_MISMATCH;
    *result = {frame->processCreation.data(), count(frame->processCreation.size()), 0};
    return frame->processSnapshot ? NKS_OK : NKS_NO_DATA;
}
NksResult __cdecl nks_process_query_identity(void* process, uint32_t kind, uint32_t size, NksProcessIdentityData* result) noexcept
{
    if (!process || !result
        || (kind != NKS_GENERATION_CREATION_TIME && kind != NKS_GENERATION_SEQUENCE_NUMBER)) return NKS_INVALID_ARGUMENT;
    if (size != sizeof(NksProcessIdentityData)) return NKS_ABI_MISMATCH;
    *result = {0, 0, 0, kind, 0, NKS_SAMPLE_UNAVAILABLE, 0};
    return invoke([&] {
        const auto queried = nks::queryProcessIdentity(process, static_cast<nks::GenerationKind>(kind));
        const auto& identity = queried.identity;
        *result = {identity.boot.low, identity.boot.high, identity.pid, static_cast<uint32_t>(identity.generationKind),
            identity.generation, static_cast<uint32_t>(queried.state), 0};
        return NKS_OK;
    });
}
int64_t __cdecl nks_monotonic_now_ns() noexcept { return nanos(std::chrono::steady_clock::now()); }
