#pragma once

#include "core/process.hpp"

namespace nks {

struct CpuSensorSample {
    std::wstring id, name;
    double celsius{};
    std::int64_t publishedAtUnixMs{};
};

class CpuSensors {
public:
    std::vector<HardwareFact> collect(bool enabled);
private:
    void read();
    std::vector<CpuSensorSample> samples;
    std::chrono::steady_clock::time_point lastRead{};
    std::int64_t lastReadUnixMs{};
    SampleState state{SampleState::unavailable};
    std::wstring detail;
};

}
