#pragma once
#include "core/process.hpp"
#include <windows.h>
#include <winternl.h>
#include <d3dkmthk.h>

namespace nks {
struct GpuAdapterFunctions {
    decltype(&D3DKMTOpenAdapterFromLuid) open{D3DKMTOpenAdapterFromLuid};
    decltype(&D3DKMTQueryAdapterInfo) query{D3DKMTQueryAdapterInfo};
    decltype(&D3DKMTCloseAdapter) close{D3DKMTCloseAdapter};
};

Metric<double> gpuTemperature(LUID luid, const GpuAdapterFunctions& functions = {});
bool gpuDisplayAlias(LUID luid);
}
