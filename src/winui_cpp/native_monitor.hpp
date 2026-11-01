#pragma once
#include "models.hpp"
#include "../interop/history_api.h"
#include "../interop/services_api.h"
#include <Windows.h>
#include <filesystem>

namespace nks::candidate {
class NativeMonitor {
public:
    explicit NativeMonitor(const std::filesystem::path& directory);
    ~NativeMonitor();
    NativeMonitor(const NativeMonitor&) = delete;
    NativeMonitor& operator=(const NativeMonitor&) = delete;
    void poll(Snapshot& snapshot);
    void pause(bool value);
    void interval(uint32_t milliseconds);
    void services(bool active);
    void selectService(const std::wstring& name);
private:
    HMODULE library_{};
    NksMonitor* monitor_{};
    NksServices* services_{};
    template<typename T> T load(const char* name);
    void processes(Snapshot& snapshot);
    void history(Snapshot& snapshot);
    void serviceData(Snapshot& snapshot);
#define NKS_FUNCTION(name) decltype(&name) name##_{ nullptr }
    NKS_FUNCTION(nks_monitor_create);
    NKS_FUNCTION(nks_monitor_destroy);
    NKS_FUNCTION(nks_monitor_read);
    NKS_FUNCTION(nks_monitor_set_paused);
    NKS_FUNCTION(nks_monitor_set_interval);
    NKS_FUNCTION(nks_monotonic_now_ns);
    NKS_FUNCTION(nks_frame_get_view);
    NKS_FUNCTION(nks_frame_get_boot_data);
    NKS_FUNCTION(nks_frame_get_process_creation_data);
    NKS_FUNCTION(nks_frame_release);
    NKS_FUNCTION(nks_monitor_read_history);
    NKS_FUNCTION(nks_history_frame_get_view);
    NKS_FUNCTION(nks_history_frame_release);
    NKS_FUNCTION(nks_services_create);
    NKS_FUNCTION(nks_services_destroy);
    NKS_FUNCTION(nks_services_read);
    NKS_FUNCTION(nks_services_set_active);
    NKS_FUNCTION(nks_services_set_paused);
    NKS_FUNCTION(nks_services_select);
    NKS_FUNCTION(nks_service_frame_get_view);
    NKS_FUNCTION(nks_service_frame_get_host_identity);
    NKS_FUNCTION(nks_service_frame_release);
#undef NKS_FUNCTION
};
}
