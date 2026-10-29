#include "gpu_telemetry.hpp"
#include <winternl.h>
#include <d3dkmthk.h>

namespace nks {

Metric<double> gpuTemperature(LUID luid)
{
    Metric<double> result{0, SampleState::unavailable, MetricSource::gpuAdapter};
    D3DKMT_OPENADAPTERFROMLUID adapter{};
    adapter.AdapterLuid = luid;
    if (D3DKMTOpenAdapterFromLuid(&adapter) < 0) return result;
    D3DKMT_ADAPTER_PERFDATA data{};
    D3DKMT_QUERYADAPTERINFO query{adapter.hAdapter, KMTQAITYPE_ADAPTERPERFDATA, &data, sizeof(data)};
    const auto status = D3DKMTQueryAdapterInfo(&query);
    D3DKMT_CLOSEADAPTER close{adapter.hAdapter};
    D3DKMTCloseAdapter(&close);
    if (status >= 0 && data.Temperature > 0 && data.Temperature < 2000) {
        result.value = data.Temperature / 10.; result.state = SampleState::valid;
    } else if (status == static_cast<NTSTATUS>(0xc00000bbu) || status == static_cast<NTSTATUS>(0xc000000du)) {
        result.state = SampleState::unsupported;
    }
    return result;
}

}
