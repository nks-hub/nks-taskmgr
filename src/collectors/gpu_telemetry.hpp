#pragma once
#include "core/process.hpp"
#include <windows.h>

namespace nks {
Metric<double> gpuTemperature(LUID luid);
bool gpuDisplayAlias(LUID luid);
}
