#include "gpu_telemetry.hpp"

namespace nks {

bool gpuDisplayAlias(LUID luid)
{
    D3DKMT_OPENADAPTERFROMLUID adapter{};
    adapter.AdapterLuid = luid;
    if (D3DKMTOpenAdapterFromLuid(&adapter) < 0) return false;
    D3DKMT_ADAPTERTYPE type{};
    D3DKMT_QUERYADAPTERINFO query{adapter.hAdapter, KMTQAITYPE_ADAPTERTYPE, &type, sizeof(type)};
    const auto status = D3DKMTQueryAdapterInfo(&query);
    D3DKMT_CLOSEADAPTER close{adapter.hAdapter};
    D3DKMTCloseAdapter(&close);
    // DXGI can expose an RDP indirect-display alias with Flags == 0.
    return status >= 0 && (type.SoftwareDevice || (type.IndirectDisplayDevice && !type.RenderSupported));
}

Metric<double> gpuTemperature(LUID luid, const GpuAdapterFunctions& functions)
{
    constexpr auto accessDenied = static_cast<NTSTATUS>(0xc0000022u);
    Metric<double> result{0, SampleState::unavailable, MetricSource::gpuAdapter};
    D3DKMT_OPENADAPTERFROMLUID adapter{};
    adapter.AdapterLuid = luid;
    const auto openStatus = functions.open(&adapter);
    if (openStatus < 0) {
        if (openStatus == accessDenied) result.state = SampleState::accessDenied;
        return result;
    }
    D3DKMT_ADAPTER_PERFDATA data{};
    D3DKMT_QUERYADAPTERINFO query{adapter.hAdapter, KMTQAITYPE_ADAPTERPERFDATA, &data, sizeof(data)};
    const auto status = functions.query(&query);
    D3DKMT_CLOSEADAPTER close{adapter.hAdapter};
    functions.close(&close);
    if (status >= 0 && data.Temperature > 0 && data.Temperature < 2000) {
        result.value = data.Temperature / 10.; result.state = SampleState::valid;
    } else if (status == static_cast<NTSTATUS>(0xc00000bbu) || status == static_cast<NTSTATUS>(0xc000000du)) {
        result.state = SampleState::unsupported;
    } else if (status == accessDenied) {
        result.state = SampleState::accessDenied;
    }
    return result;
}

}
