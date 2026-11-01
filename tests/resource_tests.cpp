#include "core/resource.hpp"
#include "core/history.hpp"
#include "core/process_sampler.hpp"
#include "collectors/gpu_telemetry.hpp"
#include <cmath>
#include <iostream>
#include <stdexcept>

namespace {
void require(bool value, const char* text) { if (!value) throw std::runtime_error(text); }
nks::NamedCounter engine(const wchar_t* name, double value, nks::SampleState state = nks::SampleState::valid)
{
    return {name, {value, state, nks::MetricSource::performanceCounter}};
}

constexpr LUID adapterLuid{0x12345678, 0x1234567};
constexpr D3DKMT_HANDLE adapterHandle = 42;
NTSTATUS openStatus{}, queryStatus{};
ULONG temperature{};
unsigned openCalls{}, queryCalls{}, closeCalls{};

NTSTATUS APIENTRY openAdapter(const D3DKMT_OPENADAPTERFROMLUID* adapter)
{
    ++openCalls;
    require(adapter->AdapterLuid.LowPart == adapterLuid.LowPart && adapter->AdapterLuid.HighPart == adapterLuid.HighPart,
        "Temperature query opened a different GPU");
    if (openStatus >= 0) const_cast<D3DKMT_OPENADAPTERFROMLUID*>(adapter)->hAdapter = adapterHandle;
    return openStatus;
}

NTSTATUS APIENTRY queryAdapter(const D3DKMT_QUERYADAPTERINFO* query)
{
    ++queryCalls;
    require(openCalls == 1 && closeCalls == 0 && query->hAdapter == adapterHandle,
        "Temperature query did not use its open adapter");
    require(query->Type == KMTQAITYPE_ADAPTERPERFDATA && query->PrivateDriverDataSize == sizeof(D3DKMT_ADAPTER_PERFDATA),
        "Wrong GPU temperature query layout");
    auto& data = *static_cast<D3DKMT_ADAPTER_PERFDATA*>(query->pPrivateDriverData);
    require(data.PhysicalAdapterIndex == 0, "Temperature query changed physical adapter index");
    data.Temperature = temperature;
    return queryStatus;
}

NTSTATUS APIENTRY closeAdapter(const D3DKMT_CLOSEADAPTER* adapter)
{
    ++closeCalls;
    require(queryCalls == 1 && adapter->hAdapter == adapterHandle, "Temperature query closed a different adapter");
    return 0;
}

void gpuTemperatureStates()
{
    constexpr auto denied = static_cast<NTSTATUS>(0xc0000022u);
    constexpr auto unsupported = static_cast<NTSTATUS>(0xc00000bbu);
    constexpr auto invalidParameter = static_cast<NTSTATUS>(0xc000000du);
    constexpr auto invalidHandle = static_cast<NTSTATUS>(0xc0000008u);
    const struct {
        const char* name;
        NTSTATUS open, query;
        ULONG rawTemperature;
        nks::SampleState state;
        double value;
    } cases[] = {
        {"open access denied", denied, 0, 615, nks::SampleState::accessDenied, 0},
        {"query access denied", 0, denied, 615, nks::SampleState::accessDenied, 0},
        {"query not supported", 0, unsupported, 615, nks::SampleState::unsupported, 0},
        {"query invalid parameter", 0, invalidParameter, 615, nks::SampleState::unsupported, 0},
        {"temperature not reported", 0, 0, 0, nks::SampleState::unavailable, 0},
        {"temperature uses tenths of Celsius", 0, 0, 615, nks::SampleState::valid, 61.5},
        {"other open failure", invalidHandle, 0, 615, nks::SampleState::unavailable, 0},
        {"other query failure", 0, invalidHandle, 615, nks::SampleState::unavailable, 0},
    };
    const nks::GpuAdapterFunctions functions{openAdapter, queryAdapter, closeAdapter};
    unsigned failures = 0;
    for (const auto& test : cases) {
        openStatus = test.open; queryStatus = test.query; temperature = test.rawTemperature;
        openCalls = queryCalls = closeCalls = 0;
        try {
            const auto result = nks::gpuTemperature(adapterLuid, functions);
            require(result.state == test.state && result.value == test.value && result.source == nks::MetricSource::gpuAdapter,
                "Wrong GPU temperature state, value or source");
            require(openCalls == 1 && queryCalls == (test.open >= 0 ? 1u : 0u) && closeCalls == queryCalls,
                "Temperature query leaked or closed an unopened adapter");
            std::cout << "PASS GPU temperature " << test.name << '\n';
        } catch (const std::exception& error) {
            ++failures;
            std::cerr << "FAIL GPU temperature " << test.name << ": " << error.what() << '\n';
        }
    }
    require(failures == 0, "GPU temperature state regressions failed");
}
}

int main()
{
    try {
        gpuTemperatureStates();
        auto rate = nks::networkRate(300, 100, 2);
        require(rate.valid() && rate.value == 800, "Network rate must use bits and actual seconds");
        require(nks::networkRate(300, 100, 0).state == nks::SampleState::gap, "Zero interval accepted");
        require(nks::networkRate(300, 100, 31).state == nks::SampleState::gap, "Suspend interval accepted");
        require(nks::networkRate(99, 100, 1).state == nks::SampleState::gap, "Network reset accepted");
        const auto luid = L"luid_0x00000000_0x00000001";
        std::vector values{
            engine(L"pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 30),
            engine(L"pid_2_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 40),
            engine(L"pid_1_luid_0x00000000_0x00000001_phys_0_eng_1_engtype_Copy", 50),
            engine(L"pid_1_luid_0x00000000_0x00000002_phys_0_eng_0_engtype_3D", 99)
        };
        require(nks::busiestGpuEngine(values, luid).value == 70, "GPU must sum processes per engine, then take maximum");
        const auto separate = nks::gpuEngines(values, luid);
        require(separate.size() == 2 && separate[0].name == L"3D" && separate[0].utilization.value == 70
            && separate[1].name == L"Copy" && separate[1].utilization.value == 50, "GPU engine identities or independent values were lost");
        values[0].metric.value = 90;
        require(nks::busiestGpuEngine(values, luid).value == 100, "GPU above 100 percent was not capped");
        values[0].metric.state = nks::SampleState::unavailable;
        require(!nks::busiestGpuEngine(values, luid).valid(), "Partial GPU counters became valid zero");
        require(!nks::gpuEngines(values, luid)[0].utilization.valid() && nks::gpuEngines(values, luid)[1].utilization.valid(), "One invalid GPU engine invalidated unrelated engines");
        require(!nks::busiestGpuEngine({}, luid).valid(), "Absent GPU counters became valid zero");
        const std::vector upper{engine(L"pid_1_luid_0x00000002_0xABCDEF01_phys_0_eng_0_engtype_3D", 17)};
        require(nks::busiestGpuEngine(upper, L"luid_0x00000002_0xabcdef01").value == 17, "GPU LUID comparison must ignore hex letter case");
        const std::vector memoryCounters{engine(L"luid_0x00000002_0xABCDEF01_phys_0", 1024), engine(L"luid_0x00000002_0xABCDEF01_phys_1", 2048), engine(L"luid_0x00000002_0xABCDEF010_phys_0", 9999)};
        require(nks::gpuMemoryUsage(memoryCounters, L"luid_0x00000002_0xabcdef01").value == 3072, "GPU adapter memory was confused with another LUID");
        require(!nks::gpuMemoryUsage({}, luid).valid(), "Missing GPU memory became measured zero");
        nks::ResourceHistory gpuHistory;
        nks::ResourceRecord gpu;
        gpu.id = luid; gpu.kind = nks::ResourceKind::gpu; gpu.engines = separate;
        const auto gpuTime = std::chrono::steady_clock::time_point(std::chrono::seconds(1));
        gpuHistory.appendGpu(gpu, gpuTime);
        gpu.engines.clear(); gpuHistory.appendGpu(gpu, gpuTime + std::chrono::seconds(1));
        gpu.engines = separate; gpuHistory.appendGpu(gpu, gpuTime + std::chrono::seconds(2));
        const auto* engineHistory = gpuHistory.find(gpu.id + separate[0].id);
        require(engineHistory && engineHistory->size() == 3 && !engineHistory->at(1).metric.valid() && engineHistory->at(2).metric.valid(), "GPU history joined across a missing engine sample");
        nks::ProcessSnapshot snapshot;
        snapshot.memory = {1024, 256, 2000, 3000, 0, 0, 0, nks::SampleState::valid};
        auto resources = nks::systemResources(snapshot);
        require(resources[1].primary.value == 768 && resources[1].secondary.value == 256, "Physical memory accounting changed");
        snapshot.memory.availableBytes = 2048;
        require(!nks::systemResources(snapshot)[1].primary.valid(), "Invalid memory counters underflowed");
        nks::ProcessSnapshot cpus;
        cpus.logicalProcessors = 2;
        cpus.measuredAt = std::chrono::steady_clock::time_point(std::chrono::seconds(1));
        cpus.processors = {{0, 0, 100, 100, 100}, {0, 1, 100, 100, 100}};
        nks::ProcessSampler sampler;
        sampler.apply(cpus);
        require(cpus.processors[0].cpuPercent.state == nks::SampleState::first, "First logical CPU sample became measured zero");
        cpus.measuredAt += std::chrono::seconds(1);
        cpus.processors[0].kernel100ns += 100;
        cpus.processors[1].kernel100ns += 100;
        cpus.processors[1].idle100ns += 100;
        sampler.apply(cpus);
        require(cpus.processors[0].cpuPercent.value == 100 && cpus.processors[1].cpuPercent.value == 0 && cpus.processors[1].cpuPercent.valid(),
            "Independent logical CPUs were averaged together");
        cpus.measuredAt += std::chrono::seconds(1);
        cpus.processors[0].group = 1;
        sampler.apply(cpus);
        require(cpus.processors[0].cpuPercent.state == nks::SampleState::gap, "Changed logical CPU identity was joined to old history");
        nks::ProcessSnapshot reset;
        reset.logicalProcessors = 2;
        reset.measuredAt = std::chrono::steady_clock::time_point(std::chrono::seconds(1));
        reset.processorState = nks::SampleState::valid;
        reset.processors = {{0, 0, 100, 0, 50}, {0, 1, 100, 0, 50}};
        reset.systemKernel100ns = 200; reset.systemIdle100ns = 100;
        nks::ProcessSampler resetSampler;
        resetSampler.apply(reset);
        reset.measuredAt += std::chrono::seconds(1);
        reset.processors = {{0, 0, 0, 0, 0}, {0, 1, 300, 0, 100}};
        reset.systemKernel100ns = 300;
        resetSampler.apply(reset);
        require(reset.systemCpuPercent.state == nks::SampleState::gap && reset.processors[1].cpuPercent.value == 75,
            "Reset of one CPU was hidden by increases on another CPU");
        reset.measuredAt += std::chrono::seconds(1);
        reset.processors = {{0, 0, 100, 0, 50}, {0, 1, 400, 0, 150}};
        reset.systemKernel100ns = 500; reset.systemIdle100ns = 200;
        resetSampler.apply(reset);
        require(reset.systemCpuPercent.valid() && reset.systemCpuPercent.value == 50, "CPU did not recover after reset baseline");
        nks::HistorySeries series;
        for (std::size_t i = 0; i < series.capacity + 20; ++i)
            series.append({std::chrono::steady_clock::time_point(std::chrono::seconds(i)), {static_cast<double>(i)}});
        require(series.size() == series.capacity && series.at(0).metric.value == 20, "History did not evict oldest point");
        series.append(series.at(0));
        require(series.at(series.size() - 1).metric.value == series.capacity + 19, "Out of order history point appended");
        nks::ResourceHistory history;
        for (std::size_t i = 0; i < history.maximumSeries + 5; ++i)
            history.append(std::to_wstring(i), {std::chrono::steady_clock::time_point(std::chrono::seconds(i)), {1}});
        require(history.size() == history.maximumSeries && !history.find(L"0") && history.find(std::to_wstring(history.maximumSeries + 4)), "Hotplug history exceeded its limit");
        std::cout << "Resource calculations and bounded history passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
