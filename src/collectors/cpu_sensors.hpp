#pragma once

#include "core/process.hpp"
#include <condition_variable>
#include <memory>
#include <mutex>
#include <thread>

namespace nks {

struct CpuSensorSample {
    std::wstring id, name;
    double celsius{};
    std::int64_t publishedAtUnixMs{};
};

class CpuSensors {
public:
    CpuSensors() = default;
    ~CpuSensors();
    std::vector<HardwareFact> collect(bool enabled);
private:
    struct Reading {
        std::vector<CpuSensorSample> samples;
        std::int64_t readAtUnixMs{};
        SampleState state{SampleState::unavailable};
        std::wstring detail;
    };
    static Reading read(std::stop_token stop);
    void run(std::stop_token stop);
    std::mutex mutex;
    std::condition_variable_any ready;
    std::shared_ptr<const Reading> latest;
    std::chrono::steady_clock::time_point lastFinished{};
    std::stop_source requestStop{std::nostopstate};
    std::uint64_t generation{}, requestGeneration{};
    bool enabled{}, queued{}, reading{}, failed{};
    std::jthread worker;
};

}
