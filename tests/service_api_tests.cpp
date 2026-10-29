#include "interop/services_api.h"
#include <windows.h>
#include <chrono>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>

extern "C" NksResult nks_test_c_services_version(NksServices** result);
namespace {
unsigned checks{};
void require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); ++checks; }
using Monitor = std::unique_ptr<NksServices, decltype(&nks_services_destroy)>;
using Frame = std::unique_ptr<NksServiceFrame, decltype(&nks_service_frame_release)>;
std::wstring copy(NksString16 value) { return std::wstring(value.data, value.length); }

void verify()
{
    using namespace std::chrono_literals;
    NksServices* raw{};
    require(nks_test_c_services_version(&raw) == NKS_ABI_MISMATCH && !raw, "C service caller accepted a wrong ABI");
    require(nks_services_create(1, nullptr) == NKS_INVALID_ARGUMENT, "Null creation output was accepted");
    require(nks_services_create(1, &raw) == NKS_OK && raw, "Service monitor did not start");
    Monitor monitor(raw, nks_services_destroy);
    require(nks_services_set_active(raw, 2) == NKS_INVALID_ARGUMENT && nks_services_set_paused(raw, -1) == NKS_INVALID_ARGUMENT, "Invalid service flags were accepted");
    require(nks_services_select(raw, nullptr, 1) == NKS_INVALID_ARGUMENT && nks_services_select(raw, L"x", 257) == NKS_INVALID_ARGUMENT, "Invalid service name bounds were accepted");
    require(nks_services_select(raw, L"x\0y", 3) == NKS_INVALID_ARGUMENT, "Embedded service terminator was accepted");
    require(nks_services_read(nullptr, nullptr) == NKS_INVALID_ARGUMENT && nks_services_set_active(nullptr, 1) == NKS_INVALID_ARGUMENT, "Null service handle was accepted");
    NksServiceFrame* pending{};
    std::this_thread::sleep_for(100ms);
    require(nks_services_read(raw, &pending) == NKS_NO_DATA && !pending, "Inactive service monitor collected data");
    require(nks_services_set_paused(raw, 1) == NKS_OK && nks_services_set_active(raw, 1) == NKS_OK, "Pause setup failed");
    std::this_thread::sleep_for(100ms);
    require(nks_services_read(raw, &pending) == NKS_NO_DATA && !pending, "Paused service monitor collected data");
    require(nks_services_select(raw, L"RpcSs", 5) == NKS_OK && nks_services_set_paused(raw, 0) == NKS_OK, "Service selection or resume failed");
    Frame snapshot(nullptr, nks_service_frame_release), detail(nullptr, nks_service_frame_release);
    NksServiceFrameView services{}, selected{};
    const auto deadline = std::chrono::steady_clock::now() + 8s;
    while (std::chrono::steady_clock::now() < deadline && (!snapshot || !detail)) {
        const auto result = nks_services_read(raw, &pending);
        require(result == NKS_OK || result == NKS_NO_DATA, "Service read failed");
        if (result == NKS_OK) {
            Frame frame(pending, nks_service_frame_release);
            NksServiceFrameView view{};
            require(nks_service_frame_get_view(pending, sizeof(view) - 1, &view) == NKS_ABI_MISMATCH, "Wrong service frame size was accepted");
            require(nks_service_frame_get_view(pending, sizeof(view), &view) == NKS_OK && view.abi_version == 1, "Service frame layout failed");
            require(view.error.length == 0 && view.detail_error.length == 0, "Service collection reported an error");
            if (view.flags & NKS_SERVICES_DATA) { nks_service_frame_retain(pending); snapshot.reset(pending); services = view; }
            if (view.flags & NKS_SERVICE_DETAIL) { nks_service_frame_retain(pending); detail.reset(pending); selected = view; }
            else require(view.detail.status_error == ERROR_NO_DATA, "A missing detail became a successful query");
        }
        std::this_thread::sleep_for(20ms);
    }
    require(snapshot && detail, "Live service snapshot and detail did not arrive");
    require(services.data.service_count > 0 && services.data.services, "Service array is empty");
    require(services.data.measured_at_ns > 0 && services.data.measured_at_ns <= services.read_at_ns, "Service timestamp is invalid");
    require(selected.detail.measured_at_ns > 0 && selected.detail.measured_at_ns <= selected.read_at_ns, "Detail timestamp is invalid");
    require(copy(selected.detail.name) == L"RpcSs" && selected.detail.status_error == 0, "Selected service detail changed identity");
    require(selected.detail.config.error == 0 && selected.detail.config.account.length > 0, "Service configuration was not exported");
    bool found = false;
    for (uint32_t i = 0; i < services.data.service_count; ++i) {
        const auto& service = services.data.services[i];
        require(service.name.length > 0 && service.name.data, "Service identity was lost");
        if (copy(service.name) == L"RpcSs") found = true;
        for (uint32_t d = 0; d < service.config.dependency_count; ++d)
            require(service.config.dependencies[d].length > 0, "Service dependency was lost");
    }
    require(found, "Selected service is missing from enumeration");
    require(nks_services_select(raw, L"RpcSs", 5) == NKS_OK, "Repeated selection failed");
    std::this_thread::sleep_for(100ms);
    require(nks_services_select(raw, L"EventLog", 8) == NKS_OK, "Changed selection failed");
    bool changed = false;
    const auto changedDeadline = std::chrono::steady_clock::now() + 5s;
    while (!changed && std::chrono::steady_clock::now() < changedDeadline) {
        const auto result = nks_services_read(raw, &pending);
        require(result == NKS_OK || result == NKS_NO_DATA, "Changed service read failed");
        if (result == NKS_OK) {
            Frame frame(pending, nks_service_frame_release); NksServiceFrameView view{};
            require(nks_service_frame_get_view(pending, sizeof(view), &view) == NKS_OK, "Changed service view failed");
            if (view.flags & NKS_SERVICE_DETAIL) {
                require(copy(view.detail.name) == L"EventLog", "A previous selection published its detail after selection changed");
                changed = true;
            }
        }
        std::this_thread::sleep_for(20ms);
    }
    require(changed, "Changed service detail did not arrive");
    const auto account = copy(selected.detail.config.account);
    monitor.reset();
    require(copy(selected.detail.config.account) == account && copy(selected.detail.name) == L"RpcSs", "Retained service frame outlived its storage");
    require(copy(services.data.services[0].name).size() > 0, "Service snapshot was released with its monitor");
    nks_service_frame_retain(detail.get());
    auto clone = detail.get(); detail.reset();
    NksServiceFrameView retained{};
    require(nks_service_frame_get_view(clone, sizeof(retained), &retained) == NKS_OK && copy(retained.detail.config.account) == account, "Retained service clone is invalid");
    nks_service_frame_release(clone);
}
}
int main()
{
    try { verify(); std::cout << checks << " service ABI checks passed\n"; return 0; }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
