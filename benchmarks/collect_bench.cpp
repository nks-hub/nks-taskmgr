#include "collectors/process_collector.hpp"
#include "core/process_sampler.hpp"
#include <psapi.h>

#include <algorithm>
#include <iomanip>
#include <iostream>

int main()
{
    try {
        nks::ProcessCollector collector;
        nks::ProcessSampler sampler;
        std::vector<double> timings;
        timings.reserve(200);
        std::size_t processCount = 0;
        for (unsigned i = 0; i < 210; ++i) {
            const auto began = std::chrono::steady_clock::now();
            auto snapshot = collector.collect();
            sampler.apply(snapshot);
            const auto ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - began).count();
            if (i >= 10) timings.push_back(ms);
            processCount = snapshot.processes.size();
        }
        PROCESS_MEMORY_COUNTERS_EX memory{};
        memory.cb = sizeof(memory);
        if (!GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory)))
            throw std::runtime_error("Cannot read benchmark process memory");
        std::sort(timings.begin(), timings.end());
        const auto median = (timings[timings.size() / 2 - 1] + timings[timings.size() / 2]) / 2;
        std::cout << std::fixed << std::setprecision(3)
            << "{\"scenario\":\"native_collection_and_sampling\",\"samples\":" << timings.size()
            << ",\"processes\":" << processCount
            << ",\"medianMs\":" << median
            << ",\"p95Ms\":" << timings[(timings.size() * 95 + 99) / 100 - 1]
            << ",\"maxMs\":" << timings.back()
            << ",\"privateBytes\":" << memory.PrivateUsage << ",\"workingSetBytes\":" << memory.WorkingSetSize << "}\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
