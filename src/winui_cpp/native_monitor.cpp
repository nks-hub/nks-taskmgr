#include "native_monitor.hpp"
#include <memory>
#include <stdexcept>

namespace nks::candidate {
namespace {
void check(NksResult result) {
    if (result != NKS_OK) throw std::runtime_error("Native monitor ABI failure: " + std::to_string(result));
}
template<typename T> void array(const T* data, uint32_t count, uint32_t maximum) {
    if (count > maximum || (count != 0 && !data)) throw std::runtime_error("Invalid native array");
}
std::vector<std::wstring> strings(const NksString16* data, uint32_t count) {
    array(data, count, 65536);
    std::vector<std::wstring> result;
    for (uint32_t i = 0; i < count; ++i) result.push_back(copy(data[i]));
    return result;
}
}
template<typename T> T NativeMonitor::load(const char* name) {
    auto address = GetProcAddress(library_, name);
    if (!address) throw std::runtime_error(std::string("Missing native ABI export: ") + name);
    return reinterpret_cast<T>(address);
}
NativeMonitor::NativeMonitor(const std::filesystem::path& directory) {
    library_ = LoadLibraryExW((directory / L"nks-monitor.dll").c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!library_) throw std::runtime_error("Cannot load app-local nks-monitor.dll");
    try {
#define NKS_LOAD(name) name##_ = load<decltype(name##_)>(#name)
        NKS_LOAD(nks_monitor_create); NKS_LOAD(nks_monitor_destroy); NKS_LOAD(nks_monitor_read);
        NKS_LOAD(nks_monitor_set_paused); NKS_LOAD(nks_monitor_set_interval); NKS_LOAD(nks_monotonic_now_ns);
        NKS_LOAD(nks_frame_get_view); NKS_LOAD(nks_frame_get_boot_data);
        NKS_LOAD(nks_frame_get_process_creation_data); NKS_LOAD(nks_frame_release);
        NKS_LOAD(nks_monitor_read_history); NKS_LOAD(nks_history_frame_get_view); NKS_LOAD(nks_history_frame_release);
        NKS_LOAD(nks_services_create); NKS_LOAD(nks_services_destroy); NKS_LOAD(nks_services_read);
        NKS_LOAD(nks_services_set_active); NKS_LOAD(nks_services_set_paused); NKS_LOAD(nks_services_select);
        NKS_LOAD(nks_service_frame_get_view); NKS_LOAD(nks_service_frame_get_host_identity); NKS_LOAD(nks_service_frame_release);
#undef NKS_LOAD
        check(nks_monitor_create_(NKS_MONITOR_ABI_VERSION, 1000, 0, &monitor_));
        check(nks_services_create_(NKS_SERVICES_ABI_VERSION, &services_));
    } catch (...) {
        if (monitor_) nks_monitor_destroy_(monitor_);
        FreeLibrary(library_);
        throw;
    }
}
NativeMonitor::~NativeMonitor() {
    if (services_) nks_services_destroy_(services_);
    if (monitor_) nks_monitor_destroy_(monitor_);
    if (library_) FreeLibrary(library_);
}
void NativeMonitor::pause(bool value) {
    check(nks_monitor_set_paused_(monitor_, value));
    check(nks_services_set_paused_(services_, value));
}
void NativeMonitor::interval(uint32_t value) { check(nks_monitor_set_interval_(monitor_, value)); }
void NativeMonitor::services(bool active) { check(nks_services_set_active_(services_, active)); }
void NativeMonitor::selectService(const std::wstring& name) {
    check(nks_services_select_(services_, name.data(), static_cast<uint32_t>(name.size())));
}
void NativeMonitor::poll(Snapshot& snapshot) {
    snapshot.processChanged = snapshot.resourceChanged = snapshot.serviceChanged = snapshot.detailChanged = false;
    snapshot.now = nks_monotonic_now_ns_();
    processes(snapshot);
    history(snapshot);
    serviceData(snapshot);
}
void NativeMonitor::processes(Snapshot& snapshot) {
    NksFrame* frame{};
    auto result = nks_monitor_read_(monitor_, &frame);
    if (result == NKS_NO_DATA) return;
    check(result);
    const std::unique_ptr<NksFrame, decltype(nks_frame_release_)> owner(frame, nks_frame_release_);
    NksFrameView view{};
    check(nks_frame_get_view_(frame, sizeof(view), &view));
    snapshot.processError = copy(view.process_error);
    snapshot.resourceError = copy(view.resource_error);
    if ((view.flags & NKS_PROCESS_DATA) != 0) {
        array(view.processes.processes, view.processes.process_count, 1048576);
        NksBootData boot{};
        check(nks_frame_get_boot_data_(frame, sizeof(boot), &boot));
        NksProcessCreationData creation{};
        check(nks_frame_get_process_creation_data_(frame, sizeof(creation), &creation));
        array(creation.processes, creation.process_count, 1048576);
        std::map<std::tuple<uint32_t, uint32_t, uint64_t>, uint64_t> times;
        for (uint32_t i = 0; i < creation.process_count; ++i) {
            const auto& value = creation.processes[i];
            if (value.state == NKS_SAMPLE_VALID)
                times[{value.pid, value.generation_kind, value.generation}] = value.created_100ns;
        }
        std::vector<Process> rows;
        rows.reserve(view.processes.process_count);
        for (uint32_t i = 0; i < view.processes.process_count; ++i) {
            const auto& value = view.processes.processes[i];
            const bool verifiedBoot = boot.state == NKS_SAMPLE_VALID;
            Process row;
            row.key = {verifiedBoot ? boot.low : 0, verifiedBoot ? boot.high : 0, value.generation, value.pid, value.generation_kind};
            row.name = copy(value.name); row.parent = value.parent_pid;
            row.created = times[{value.pid, value.generation_kind, value.generation}];
            row.threads = value.threads; row.handles = value.handles;
            row.workingSet = value.working_set_bytes; row.privateBytes = value.private_bytes;
            row.cpu = value.cpu_percent; row.read = value.read_io_bytes_per_second; row.write = value.write_io_bytes_per_second;
            rows.push_back(std::move(row));
        }
        snapshot.processes = std::move(rows);
        snapshot.processTime = view.processes.measured_at_ns;
        snapshot.processChanged = true;
        const auto& memory = view.processes.memory;
        auto memoryState = memory.state;
        if (memoryState == NKS_SAMPLE_VALID && memory.available_bytes > memory.physical_bytes) memoryState = NKS_SAMPLE_ERROR;
        const auto used = memory.available_bytes <= memory.physical_bytes ? memory.physical_bytes - memory.available_bytes : 0;
        std::erase_if(snapshot.resources, [](const auto& resource) { return resource.kind <= NKS_RESOURCE_MEMORY; });
        snapshot.resources.insert(snapshot.resources.begin(), {
            Resource{NKS_RESOURCE_CPU, L"cpu", L"Procesor", copy(view.processes.cpu_model), L"", view.processes.cpu_percent,
                {0, NKS_SAMPLE_UNAVAILABLE, NKS_SOURCE_PROCESSOR_TIME}, {0, NKS_SAMPLE_UNAVAILABLE, NKS_SOURCE_PROCESSOR_TIME},
                100, snapshot.processTime},
            Resource{NKS_RESOURCE_MEMORY, L"memory", L"Paměť", L"Fyzická paměť", L"",
                {static_cast<double>(used), memoryState, NKS_SOURCE_PERFORMANCE_INFO},
                {static_cast<double>(memory.available_bytes), memoryState, NKS_SOURCE_PERFORMANCE_INFO},
                {static_cast<double>(memory.committed_bytes), memoryState, NKS_SOURCE_PERFORMANCE_INFO},
                static_cast<double>(memory.physical_bytes), snapshot.processTime}});
        snapshot.resourceChanged = true;
    }
    if ((view.flags & NKS_RESOURCE_DATA) != 0) {
        array(view.resources.resources, view.resources.resource_count, 4096);
        std::vector<Resource> rows;
        for (const auto& resource : snapshot.resources) if (resource.kind <= NKS_RESOURCE_MEMORY) rows.push_back(resource);
        for (uint32_t i = 0; i < view.resources.resource_count; ++i) {
            const auto& value = view.resources.resources[i];
            rows.push_back({value.kind, copy(value.id), copy(value.title), copy(value.description), copy(value.status),
                value.primary, value.secondary, value.tertiary, value.capacity, view.resources.measured_at_ns});
        }
        snapshot.resources = std::move(rows);
        snapshot.resourceTime = view.resources.measured_at_ns;
        snapshot.resourceChanged = true;
    }
}
void NativeMonitor::history(Snapshot& snapshot) {
    for (int batch = 0; batch < 4; ++batch) {
        NksHistoryFrame* frame{};
        auto result = nks_monitor_read_history_(monitor_, &frame);
        if (result == NKS_NO_DATA) return;
        check(result);
        const std::unique_ptr<NksHistoryFrame, decltype(nks_history_frame_release_)> owner(frame, nks_history_frame_release_);
        NksHistoryView view{};
        check(nks_history_frame_get_view_(frame, sizeof(view), &view));
        array(view.definitions, view.definition_count, 512); array(view.points, view.point_count, 65536);
        for (uint32_t i = 0; i < view.definition_count; ++i) {
            const auto& value = view.definitions[i];
            if (!snapshot.series.contains(value.id))
                snapshot.series.emplace(value.id, Series{value.channel, value.group, value.index,
                    copy(value.resource_id), copy(value.detail_id), copy(value.name), {}});
        }
        auto interrupt = [&] { for (auto& [id, series] : snapshot.series) { (void)id; series.history.interrupted = true; } };
        snapshot.lost += view.lost_points; snapshot.captureErrors += view.capture_errors;
        if (view.lost_points || view.capture_errors) interrupt();
        for (uint32_t i = 0; i < view.point_count; ++i) {
            const auto& value = view.points[i];
            auto found = snapshot.series.find(value.series);
            if (found == snapshot.series.end() || !value.sequence || value.time_ns <= 0)
                throw std::runtime_error("Invalid native history definition/point");
            if (value.sequence <= snapshot.historySequence) continue;
            if (snapshot.historySequence && value.sequence != snapshot.historySequence + 1) interrupt();
            snapshot.historySequence = value.sequence;
            found->second.history.append(value.time_ns, value.metric);
        }
        if (!view.more_pending) return;
    }
}
void NativeMonitor::serviceData(Snapshot& snapshot) {
    NksServiceFrame* frame{};
    auto result = nks_services_read_(services_, &frame);
    if (result == NKS_NO_DATA) return;
    check(result);
    const std::unique_ptr<NksServiceFrame, decltype(nks_service_frame_release_)> owner(frame, nks_service_frame_release_);
    NksServiceFrameView view{};
    check(nks_service_frame_get_view_(frame, sizeof(view), &view));
    snapshot.serviceError = copy(view.error);
    if ((view.flags & NKS_SERVICES_DATA) != 0) {
        array(view.data.services, view.data.service_count, 65536);
        std::vector<Service> rows;
        for (uint32_t i = 0; i < view.data.service_count; ++i) {
            const auto& value = view.data.services[i];
            rows.push_back({copy(value.name), copy(value.title), copy(value.config.account), copy(value.config.command),
                copy(value.config.group), strings(value.config.dependencies, value.config.dependency_count),
                value.state, value.pid, value.config.start_type, value.config.error});
        }
        snapshot.services = std::move(rows); snapshot.serviceChanged = true;
        snapshot.serviceTime = view.data.measured_at_ns;
    }
    if ((view.flags & NKS_SERVICE_DETAIL) != 0) {
        NksProcessIdentityData identity{};
        check(nks_service_frame_get_host_identity_(frame, NKS_GENERATION_SEQUENCE_NUMBER, sizeof(identity), &identity));
        snapshot.detail = {copy(view.detail.name), copy(view.detail.description), copy(view.detail_error),
            strings(view.detail.dependents, view.detail.dependent_count),
            {identity.boot_low, identity.boot_high, identity.generation, identity.pid, identity.generation_kind},
            view.detail.host_created_100ns, identity.state};
        snapshot.detailChanged = true;
    }
}
}
