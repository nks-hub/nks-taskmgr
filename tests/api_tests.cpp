#include "interop/monitor_api.h"
#include <windows.h>
#include <chrono>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>

extern "C" NksResult nks_test_c_version(NksMonitor** result);

namespace {
unsigned checks{};
void require(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
    ++checks;
}
using Monitor = std::unique_ptr<NksMonitor, decltype(&nks_monitor_destroy)>;
using Frame = std::unique_ptr<NksFrame, decltype(&nks_frame_release)>;

void verify()
{
    using namespace std::chrono_literals;
    NksMonitor* raw{};
    require(nks_test_c_version(&raw) == NKS_ABI_MISMATCH && !raw, "C caller ABI mismatch was accepted");
    require(nks_monitor_create(NKS_MONITOR_ABI_VERSION, 0, 1, &raw) == NKS_INVALID_ARGUMENT && !raw, "Invalid cadence created a monitor");
    require(nks_monitor_create(NKS_MONITOR_ABI_VERSION, 250, 2, &raw) == NKS_INVALID_ARGUMENT && !raw, "Invalid pause flag created a monitor");
    require(nks_monitor_create(NKS_MONITOR_ABI_VERSION, 250, 1, nullptr) == NKS_INVALID_ARGUMENT, "Null output was accepted");
    require(nks_monitor_set_interval(nullptr, 250) == NKS_INVALID_ARGUMENT && nks_monitor_set_paused(nullptr, 1) == NKS_INVALID_ARGUMENT, "Null monitor was accepted");
    require(nks_monitor_create(NKS_MONITOR_ABI_VERSION, 250, 1, &raw) == NKS_OK && raw, "The ABI monitor did not start");
    Monitor monitor(raw, nks_monitor_destroy);
    require(nks_monitor_set_interval(raw, 0) == NKS_INVALID_ARGUMENT && nks_monitor_set_paused(raw, -1) == NKS_INVALID_ARGUMENT, "Invalid controls were accepted");
    std::this_thread::sleep_for(300ms);
    NksFrame* pending{};
    require(nks_monitor_read(raw, &pending) == NKS_NO_DATA && !pending, "Paused ABI monitor produced a frame");
    require(nks_monitor_read(nullptr, &pending) == NKS_INVALID_ARGUMENT && !pending, "Null read was accepted");
    require(nks_monitor_set_paused(raw, 0) == NKS_OK, "ABI monitor did not resume");
    Frame processFrame(nullptr, nks_frame_release), resourceFrame(nullptr, nks_frame_release);
    NksFrameView processes{}, resources{};
    const auto deadline = std::chrono::steady_clock::now() + 8s;
    while (std::chrono::steady_clock::now() < deadline) {
        const auto status = nks_monitor_read(raw, &pending);
        require(status == NKS_OK || status == NKS_NO_DATA, "ABI read failed");
        if (status == NKS_OK) {
            Frame frame(pending, nks_frame_release);
            NksFrameView view{};
            require(nks_frame_get_view(frame.get(), sizeof(view) - 1, &view) == NKS_ABI_MISMATCH, "A wrong view layout was accepted");
            require(nks_frame_get_view(frame.get(), sizeof(view), &view) == NKS_OK && view.abi_version == NKS_MONITOR_ABI_VERSION, "ABI view layout mismatch");
            require(view.process_error.length == 0 && view.resource_error.length == 0, "ABI live collection reported an error");
            if (!(view.flags & NKS_PROCESS_DATA)) require(view.processes.cpu_percent.state == NKS_SAMPLE_UNAVAILABLE
                && view.processes.memory.state == NKS_SAMPLE_UNAVAILABLE, "A missing process stream became measured zero");
            if ((view.flags & NKS_PROCESS_DATA) && view.processes.cpu_percent.state == NKS_SAMPLE_VALID) {
                nks_frame_retain(frame.get()); processFrame.reset(frame.get()); processes = view;
            }
            if (view.flags & NKS_RESOURCE_DATA) {
                nks_frame_retain(frame.get()); resourceFrame.reset(frame.get()); resources = view;
            }
        }
        if (processFrame && resourceFrame) break;
        std::this_thread::sleep_for(20ms);
    }
    require(processFrame && resourceFrame, "Both ABI streams did not arrive");
    NksBootData boot{};
    require(nks_frame_get_boot_data(nullptr, sizeof(boot), &boot) == NKS_INVALID_ARGUMENT, "Null boot frame was accepted");
    require(nks_frame_get_boot_data(processFrame.get(), sizeof(boot), nullptr) == NKS_INVALID_ARGUMENT, "Null boot output was accepted");
    require(nks_frame_get_boot_data(processFrame.get(), sizeof(boot) - 1, &boot) == NKS_ABI_MISMATCH, "Wrong boot layout was accepted");
    require(nks_frame_get_boot_data(processFrame.get(), sizeof(boot), &boot) == NKS_OK && boot.state == NKS_SAMPLE_VALID
        && (boot.low || boot.high), "ABI boot GUID was not available");
    const auto retainedBoot = boot;
    const auto& p = processes.processes;
    require(p.process_count > 0 && p.processes && p.processor_count == p.logical_processors && p.processors, "ABI arrays were not complete");
    require(p.cpu_percent.value >= 0 && p.cpu_percent.value <= 100, "ABI CPU normalization failed");
    const NksProcess* own{};
    for (uint32_t i = 0; i < p.process_count; ++i) if (p.processes[i].pid == GetCurrentProcessId()) own = &p.processes[i];
    require(own && (own->generation_kind == NKS_GENERATION_CREATION_TIME || own->generation_kind == NKS_GENERATION_SEQUENCE_NUMBER)
        && own->generation && own->name.length && own->name.data, "ABI process identity or name was lost");
    NksProcessIdentityData ownIdentity{};
    require(nks_process_query_identity(GetCurrentProcess(), own->generation_kind, sizeof(ownIdentity), &ownIdentity) == NKS_OK
        && ownIdentity.state == NKS_SAMPLE_VALID && ownIdentity.generation_kind == own->generation_kind
        && ownIdentity.generation == own->generation, "ABI process identity differs from the held process");
    const auto name = std::wstring(own->name.data, own->name.length);
    const auto generation = own->generation;
    require(p.memory.state == NKS_SAMPLE_VALID && p.memory.available_bytes <= p.memory.physical_bytes, "ABI memory fields were misaligned");
    require(p.measured_at_ns <= processes.read_at_ns && processes.read_at_ns <= nks_monotonic_now_ns(), "ABI timestamps use different clocks");
    for (uint32_t i = 0; i < resources.resources.resource_count; ++i) {
        const auto& resource = resources.resources.resources[i];
        require(resource.kind <= NKS_RESOURCE_NETWORK && resource.id.length && resource.id.data, "ABI device identity was lost");
    }
    monitor.reset();
    require(nks_frame_get_boot_data(processFrame.get(), sizeof(boot), &boot) == NKS_OK
        && boot.low == retainedBoot.low && boot.high == retainedBoot.high && boot.state == retainedBoot.state,
        "Boot identity depended on the destroyed monitor");
    NksHardwareData hardware{};
    require(nks_frame_get_hardware_data(nullptr, sizeof(hardware), &hardware) == NKS_INVALID_ARGUMENT, "Null hardware frame was accepted");
    require(nks_frame_get_hardware_data(processFrame.get(), sizeof(hardware) - 1, &hardware) == NKS_ABI_MISMATCH, "Wrong hardware layout was accepted");
    require(nks_frame_get_hardware_data(processFrame.get(), sizeof(hardware), &hardware) == NKS_OK && hardware.fact_count > 0, "Hardware data depended on the destroyed monitor");
    for (uint32_t i = 0; i < hardware.fact_count; ++i) require(hardware.facts[i].resource_id.length && hardware.facts[i].name.length && hardware.facts[i].value.length, "Hardware strings were invalidated");
    NksGpuData gpuData{};
    require(nks_frame_get_gpu_data(nullptr, sizeof(gpuData), &gpuData) == NKS_INVALID_ARGUMENT, "Null GPU frame was accepted");
    require(nks_frame_get_gpu_data(resourceFrame.get(), sizeof(gpuData) - 1, &gpuData) == NKS_ABI_MISMATCH, "Wrong GPU layout was accepted");
    require(nks_frame_get_gpu_data(resourceFrame.get(), sizeof(gpuData), &gpuData) == NKS_OK, "GPU frame depended on the destroyed monitor");
    for (uint32_t i = 0; i < gpuData.gpu_count; ++i) {
        const auto& gpu = gpuData.gpus[i];
        require(gpu.resource_id.length > 0 && gpu.shared_capacity >= 0, "GPU capacity or identity was lost");
        for (uint32_t j = 0; j < gpu.engine_count; ++j) require(gpu.engines[j].id.length > 0 && gpu.engines[j].name.length > 0, "GPU engine string was invalidated");
    }
    require(own->generation == generation && std::wstring(own->name.data, own->name.length) == name, "A frame depended on a destroyed monitor");
    NksFrameView retained{};
    require(nks_frame_get_view(processFrame.get(), sizeof(retained), &retained) == NKS_OK && retained.processes.process_count == p.process_count, "A retained frame was invalidated");
    require(nks_frame_get_view(nullptr, sizeof(retained), &retained) == NKS_INVALID_ARGUMENT, "A null frame was accepted");
}
}

int main()
{
    try { verify(); std::cout << checks << " C ABI checks passed\n"; return 0; }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
