#include "collectors/process_collector.hpp"
#include "core/process_sampler.hpp"
#include <psapi.h>

#include <algorithm>
#include <array>
#include <iomanip>
#include <iostream>
#include <string_view>

int main(int argc, char** argv)
{
    try {
        // --sequence matches the collector worker, which adds two identity queries where Windows supports them.
        const bool sequence = argc > 1 && std::string_view(argv[1]) == "--sequence";
        nks::ProcessCollector collector(sequence ? nks::ProcessIdentityPreference::sequenceNumberWhenSupported
                                                 : nks::ProcessIdentityPreference::creationTime);
        nks::ProcessSampler sampler;
        std::array<std::vector<double>, 5> timings;
        for (auto& stage : timings) stage.reserve(200);
        std::size_t processCount = 0;
        for (unsigned i = 0; i < 210; ++i) {
            const auto began = std::chrono::steady_clock::now();
            auto snapshot = collector.collect();
            const auto samplingAt = std::chrono::steady_clock::now();
            sampler.apply(snapshot);
            const auto ended = std::chrono::steady_clock::now();
            const std::array values{
                std::chrono::duration<double, std::milli>(ended - began).count(),
                std::chrono::duration<double, std::milli>(snapshot.queryTime).count(),
                std::chrono::duration<double, std::milli>(snapshot.parseTime).count(),
                std::chrono::duration<double, std::milli>(snapshot.systemTime).count(),
                std::chrono::duration<double, std::milli>(ended - samplingAt).count()
            };
            if (i >= 10) for (std::size_t stage = 0; stage < values.size(); ++stage) timings[stage].push_back(values[stage]);
            processCount = snapshot.processes.size();
        }
        PROCESS_MEMORY_COUNTERS_EX memory{};
        memory.cb = sizeof(memory);
        if (!GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory)))
            throw std::runtime_error("Cannot read benchmark process memory");
        for (auto& stage : timings) std::sort(stage.begin(), stage.end());
        const auto& total = timings[0];
        const auto median = (total[total.size() / 2 - 1] + total[total.size() / 2]) / 2;
        std::cout << std::fixed << std::setprecision(3)
            << "{\"scenario\":\"native_collection_and_sampling\",\"samples\":" << total.size()
            << ",\"identity\":\"" << (sequence ? "sequenceNumberWhenSupported" : "creationTime") << '"'
            << ",\"processes\":" << processCount
            << ",\"medianMs\":" << median
            << ",\"p95Ms\":" << total[(total.size() * 95 + 99) / 100 - 1]
            << ",\"maxMs\":" << total.back();
        const std::array names{"query", "parse", "system", "sampling"};
        for (std::size_t i = 0; i < names.size(); ++i) {
            const auto& stage = timings[i + 1];
            std::cout << ",\"" << names[i] << "P95Ms\":" << stage[(stage.size() * 95 + 99) / 100 - 1]
                << ",\"" << names[i] << "MaxMs\":" << stage.back();
        }
        std::cout << ",\"privateBytes\":" << memory.PrivateUsage << ",\"workingSetBytes\":" << memory.WorkingSetSize << "}\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
