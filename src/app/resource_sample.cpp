#include "collectors/resource_collector.hpp"
#include <iostream>
#include <set>
#include <thread>

int main()
{
    try {
        nks::ResourceCollector collector;
        auto before = collector.collect();
        std::this_thread::sleep_for(std::chrono::seconds(1));
        const auto began = std::chrono::steady_clock::now();
        auto after = collector.collect();
        std::set<std::wstring> identities;
        unsigned disks = 0, gpus = 0, networks = 0, validDisks = 0, validGpus = 0, disconnected = 0;
        for (const auto& resource : after.devices) {
            if (resource.id.empty() || !identities.insert(resource.id).second)
                throw std::runtime_error("Device identity is empty or duplicated");
            if (resource.kind == nks::ResourceKind::disk) { ++disks; if (resource.primary.valid()) ++validDisks; }
            if (resource.kind == nks::ResourceKind::gpu) { ++gpus; if (resource.primary.valid()) ++validGpus; }
            if (resource.kind == nks::ResourceKind::network) { ++networks; if (resource.status == L"Odpojeno") ++disconnected; }
            if (resource.status == L"Odpojeno" && resource.primary.valid())
                throw std::runtime_error("Disconnected network has a valid utilization");
        }
        if (after.measuredAt <= before.measuredAt)
            throw std::runtime_error("Device timestamp did not advance");
        std::cout << "{\"disks\":" << disks << ",\"validDisks\":" << validDisks
            << ",\"gpus\":" << gpus << ",\"validGpus\":" << validGpus
            << ",\"networks\":" << networks << ",\"disconnected\":" << disconnected
            << ",\"collectionMs\":" << std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - began).count() << "}\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
