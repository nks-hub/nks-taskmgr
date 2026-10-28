#include "collectors/process_collector.hpp"
#include "core/process_sampler.hpp"

#include <algorithm>
#include <atomic>
#include <cstring>
#include <iomanip>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <thread>

namespace {

void require(bool condition, const char* message)
{
    if (!condition)
        throw std::runtime_error(message);
}

}

int main(int argc, char** argv)
{
    try {
        if (argc > 2 || (argc == 2 && std::strcmp(argv[1], "--verify") != 0)) {
            std::cerr << "Usage: nks-sample [--verify]\n";
            return 2;
        }
        const bool verify = argc == 2;
        nks::ProcessCollector collector;
        nks::ProcessSampler sampler;
        auto before = collector.collect();
        sampler.apply(before);
        const auto findSelf = [](const auto& snapshot) {
            return std::find_if(snapshot.processes.begin(), snapshot.processes.end(), [](const auto& process) {
                return process.identity.pid == GetCurrentProcessId();
            });
        };
        const auto selfBefore = findSelf(before);
        require(selfBefore != before.processes.end(), "Collector did not find its own process");

        constexpr std::size_t allocationSize = 32 * 1024 * 1024;
        auto freePages = [](void* memory) { if (memory) VirtualFree(memory, 0, MEM_RELEASE); };
        std::unique_ptr<void, decltype(freePages)> allocation(nullptr, freePages);
        std::jthread load;
        if (verify) {
            allocation.reset(VirtualAlloc(nullptr, allocationSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
            require(allocation != nullptr, "Cannot allocate verification pages");
            std::memset(allocation.get(), 0x5a, allocationSize);
            load = std::jthread([](std::stop_token stop) {
                std::atomic<std::uint64_t> iterations{};
                while (!stop.stop_requested())
                    iterations.fetch_add(1, std::memory_order_relaxed);
            });
        }
        std::this_thread::sleep_for(std::chrono::seconds(1));
        auto after = collector.collect();
        sampler.apply(after);
        load.request_stop();
        if (load.joinable()) load.join();

        const auto selfAfter = findSelf(after);
        require(selfAfter != after.processes.end(), "Own process disappeared from collection");
        require(after.memory.state == nks::SampleState::valid && after.memory.physicalBytes > 0, "Physical memory is unavailable");
        if (verify) {
            require(after.systemCpuPercent.valid() && after.systemCpuPercent.value >= 0 && after.systemCpuPercent.value <= 100,
                "System CPU counters are unavailable or invalid");
            FILETIME created{}, exited{}, kernel{}, user{};
            require(GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user) != FALSE, "Cannot verify creation time");
            const auto generation = (static_cast<std::uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
            require(selfAfter->identity.generation == generation, "Process creation time does not match the live handle");
            require(selfAfter->identity == selfBefore->identity, "Stable process identity changed between samples");
            require(selfAfter->cpuPercent.valid() && selfAfter->cpuPercent.value > 0, "Controlled CPU load was not measured");
            require(selfAfter->counters.privateBytes >= selfBefore->counters.privateBytes
                && selfAfter->counters.privateBytes - selfBefore->counters.privateBytes >= allocationSize * 3 / 4,
                "Known private memory allocation was not measured");
        }
        const auto valid = std::count_if(after.processes.begin(), after.processes.end(), [](const auto& process) { return process.cpuPercent.valid(); });
        std::cout << std::fixed << std::setprecision(3)
            << "{\"processes\":" << after.processes.size() << ",\"logicalProcessors\":" << after.logicalProcessors
            << ",\"validProcessCpu\":" << valid << ",\"systemCpuState\":" << static_cast<int>(after.systemCpuPercent.state)
            << ",\"systemCpuPercent\":" << after.systemCpuPercent.value
            << ",\"physicalBytes\":" << after.memory.physicalBytes << ",\"availableBytes\":" << after.memory.availableBytes
            << ",\"collectionMs\":" << std::chrono::duration<double, std::milli>(after.collectionTime).count()
            << ",\"verification\":" << (verify ? "true" : "false") << "}\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
