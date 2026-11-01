#include "services_api.h"
#include "core/service_monitor.hpp"
#include <array>
#include <limits>
#include <stdexcept>

struct NksServices {
    nks::ServiceMonitor monitor;
    std::mutex readMutex;
    std::wstring error, detailError;
};

struct NksServiceFrame {
    std::atomic<uint32_t> references{1};
    std::shared_ptr<const nks::ServiceSnapshot> snapshot;
    std::shared_ptr<const nks::ServiceDetail> detail;
    std::wstring error, detailError;
    std::vector<NksService> services;
    std::vector<std::vector<NksString16>> dependencies;
    std::vector<NksString16> detailDependencies, dependents;
    NksServiceFrameView view{};
    std::array<NksProcessIdentityData, 2> hostIdentities{{
        {0, 0, 0, NKS_GENERATION_CREATION_TIME, 0, NKS_SAMPLE_UNAVAILABLE, 0},
        {0, 0, 0, NKS_GENERATION_SEQUENCE_NUMBER, 0, NKS_SAMPLE_UNAVAILABLE, 0}}};
    void build();
};

namespace {
template<class Action> NksResult invoke(Action&& action) noexcept
{
    try { return action(); }
    catch (const std::invalid_argument&) { return NKS_INVALID_ARGUMENT; }
    catch (const std::bad_alloc&) { return NKS_OUT_OF_MEMORY; }
    catch (...) { return NKS_FAILED; }
}
uint32_t count(std::size_t value)
{
    if (value > std::numeric_limits<uint32_t>::max()) throw std::length_error("Service count exceeds the ABI limit");
    return static_cast<uint32_t>(value);
}
NksString16 string(const std::wstring& value) { return {value.data(), count(value.size()), 0}; }
int64_t nanos(std::chrono::steady_clock::time_point value) { return std::chrono::duration_cast<std::chrono::nanoseconds>(value.time_since_epoch()).count(); }
void strings(const std::vector<std::wstring>& source, std::vector<NksString16>& target)
{
    target.reserve(source.size());
    for (const auto& value : source) target.push_back(string(value));
}
NksServiceConfig config(const nks::ServiceConfig& source, std::vector<NksString16>& dependencies)
{
    strings(source.dependencies, dependencies);
    return {source.error, source.startType, source.delayedError, source.delayed ? 1u : 0u,
        string(source.account), string(source.group), string(source.command), dependencies.data(), count(dependencies.size()), 0};
}

void readHostIdentities(const nks::ServiceDetail& detail, std::array<NksProcessIdentityData, 2>& identities)
{
    const auto unavailable = [&](uint32_t state) { for (auto& identity : identities) identity.state = state; };
    if (detail.statusError || detail.hostError) {
        const auto error = detail.statusError ? detail.statusError : detail.hostError;
        unavailable(error == ERROR_ACCESS_DENIED ? NKS_SAMPLE_ACCESS_DENIED
            : error == ERROR_NO_DATA ? NKS_SAMPLE_UNAVAILABLE : NKS_SAMPLE_ERROR);
        return;
    }
    if (detail.state != SERVICE_RUNNING || !detail.pid || !detail.hostCreated100ns) return;
    const std::unique_ptr<void, decltype(&CloseHandle)> process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, detail.pid), CloseHandle);
    if (!process) { unavailable(GetLastError() == ERROR_ACCESS_DENIED ? NKS_SAMPLE_ACCESS_DENIED : NKS_SAMPLE_UNAVAILABLE); return; }
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process.get(), &created, &exited, &kernel, &user)) {
        unavailable(GetLastError() == ERROR_ACCESS_DENIED ? NKS_SAMPLE_ACCESS_DENIED : NKS_SAMPLE_ERROR); return;
    }
    const auto created100ns = (static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
    if (created100ns != detail.hostCreated100ns) { unavailable(NKS_SAMPLE_STALE); return; }
    for (auto& identity : identities) {
        const auto kind = identity.generation_kind;
        if (nks_process_query_identity(process.get(), kind, sizeof(identity), &identity) != NKS_OK)
            identity = {0, 0, 0, kind, 0, NKS_SAMPLE_ERROR, 0};
    }
}
}

static_assert(sizeof(NksServiceConfig) == 80 && sizeof(NksService) == 128);
static_assert(sizeof(NksServiceData) == 32 && sizeof(NksServiceDetail) == 168 && sizeof(NksServiceFrameView) == 248);

void NksServiceFrame::build()
{
    view.abi_version = NKS_SERVICES_ABI_VERSION;
    view.read_at_ns = nks_monotonic_now_ns();
    view.error = string(error); view.detail_error = string(detailError);
    view.detail.status_error = view.detail.config.error = ERROR_NO_DATA;
    if (snapshot) {
        services.reserve(snapshot->services.size()); dependencies.reserve(snapshot->services.size());
        for (const auto& service : snapshot->services) {
            dependencies.emplace_back();
            services.push_back({string(service.name), string(service.title), service.state, service.pid, service.type,
                service.acceptedControls, config(service.config, dependencies.back())});
        }
        view.data = {nanos(snapshot->measuredAt), snapshot->collectionTime.count(), services.data(), count(services.size()), 0};
    }
    if (detail) {
        strings(detail->dependents, dependents);
        view.detail = {nanos(detail->measuredAt), string(detail->name), string(detail->description), config(detail->config, detailDependencies),
            detail->statusError, detail->descriptionError, detail->dependentsError, detail->hostError,
            detail->pid, detail->state, detail->hostCreated100ns, dependents.data(), count(dependents.size()), 0};
        readHostIdentities(*detail, hostIdentities);
    }
}

NksResult __cdecl nks_services_create(uint32_t version, NksServices** result) noexcept
{
    if (!result) return NKS_INVALID_ARGUMENT;
    *result = nullptr;
    if (version != NKS_SERVICES_ABI_VERSION) return NKS_ABI_MISMATCH;
    return invoke([&] {
        auto instance = std::make_unique<NksServices>();
        instance->monitor.start(); *result = instance.release(); return NKS_OK;
    });
}
void __cdecl nks_services_destroy(NksServices* services) noexcept { delete services; }
NksResult __cdecl nks_services_set_active(NksServices* services, int32_t active) noexcept
{
    if (!services || (active != 0 && active != 1)) return NKS_INVALID_ARGUMENT;
    return invoke([&] { services->monitor.setActive(active != 0); return NKS_OK; });
}
NksResult __cdecl nks_services_set_paused(NksServices* services, int32_t paused) noexcept
{
    if (!services || (paused != 0 && paused != 1)) return NKS_INVALID_ARGUMENT;
    return invoke([&] { services->monitor.setPaused(paused != 0); return NKS_OK; });
}
NksResult __cdecl nks_services_select(NksServices* services, const wchar_t* name, uint32_t length) noexcept
{
    if (!services || length > 256 || (!name && length)) return NKS_INVALID_ARGUMENT;
    return invoke([&] {
        std::wstring selected(name ? name : L"", length);
        if (selected.find(L'\0') != std::wstring::npos) return NKS_INVALID_ARGUMENT;
        std::lock_guard lock(services->readMutex);
        services->monitor.select(std::move(selected)); services->detailError.clear(); return NKS_OK;
    });
}
NksResult __cdecl nks_services_read(NksServices* services, NksServiceFrame** result) noexcept
{
    if (!result) return NKS_INVALID_ARGUMENT;
    *result = nullptr;
    if (!services) return NKS_INVALID_ARGUMENT;
    return invoke([&] {
        std::lock_guard lock(services->readMutex);
        auto frame = std::make_unique<NksServiceFrame>();
        frame->snapshot = services->monitor.take(frame->error);
        frame->detail = services->monitor.takeDetail(frame->detailError);
        if (frame->snapshot) frame->view.flags |= NKS_SERVICES_DATA;
        if (frame->detail) frame->view.flags |= NKS_SERVICE_DETAIL;
        if (frame->error != services->error) frame->view.flags |= NKS_SERVICES_ERROR_CHANGED;
        if (frame->detailError != services->detailError) frame->view.flags |= NKS_SERVICE_DETAIL_ERROR_CHANGED;
        if (!frame->view.flags) return NKS_NO_DATA;
        frame->build();
        auto error = frame->error, detailError = frame->detailError;
        services->error.swap(error); services->detailError.swap(detailError);
        *result = frame.release(); return NKS_OK;
    });
}
NksResult __cdecl nks_service_frame_get_view(const NksServiceFrame* frame, uint32_t size, NksServiceFrameView* result) noexcept
{
    if (!frame || !result) return NKS_INVALID_ARGUMENT;
    if (size != sizeof(NksServiceFrameView)) return NKS_ABI_MISMATCH;
    *result = frame->view; return NKS_OK;
}
void __cdecl nks_service_frame_retain(NksServiceFrame* frame) noexcept { if (frame) frame->references.fetch_add(1, std::memory_order_relaxed); }
NksResult __cdecl nks_service_frame_get_host_identity(const NksServiceFrame* frame, uint32_t kind, uint32_t size, NksProcessIdentityData* result) noexcept
{
    if (!frame || !result || (kind != NKS_GENERATION_CREATION_TIME && kind != NKS_GENERATION_SEQUENCE_NUMBER)) return NKS_INVALID_ARGUMENT;
    if (size != sizeof(NksProcessIdentityData)) return NKS_ABI_MISMATCH;
    *result = frame->hostIdentities[kind == NKS_GENERATION_CREATION_TIME ? 0 : 1];
    return frame->detail ? NKS_OK : NKS_NO_DATA;
}
void __cdecl nks_service_frame_release(NksServiceFrame* frame) noexcept
{
    if (frame && frame->references.fetch_sub(1, std::memory_order_acq_rel) == 1) delete frame;
}
