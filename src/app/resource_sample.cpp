#include "collectors/resource_collector.hpp"
#include <iostream>
#include <set>
#include <thread>

namespace {
bool elevated()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) throw std::runtime_error("Cannot query the sample process token");
    TOKEN_ELEVATION elevation{};
    DWORD used = 0;
    const auto success = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &used);
    CloseHandle(token);
    if (!success || used < sizeof(elevation)) throw std::runtime_error("Cannot read the sample elevation state");
    return elevation.TokenIsElevated != 0;
}

std::string utf8(const std::wstring& text)
{
    const auto count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (!count) throw std::runtime_error("Disk facts contain invalid UTF-16");
    std::string output(static_cast<std::size_t>(count), '\0');
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), output.data(), count, nullptr, nullptr))
        throw std::runtime_error("Disk facts could not be encoded");
    return output;
}
}

int main(int argc, char** argv)
{
    try {
        const bool details = argc == 2 && std::string(argv[1]) == "--disk-details";
        if (argc > 1 && !details) throw std::runtime_error("Usage: nks-resource-sample [--disk-details]");
        nks::ResourceCollector collector;
        auto before = collector.collect();
        std::this_thread::sleep_for(std::chrono::seconds(1));
        const auto began = std::chrono::steady_clock::now();
        auto after = collector.collect();
        std::set<std::wstring> identities;
        unsigned disks = 0, gpus = 0, networks = 0, validDisks = 0, validGpus = 0, disconnected = 0;
        unsigned gpuEngines = 0, validGpuMemory = 0;
        for (const auto& resource : after.devices) {
            if (resource.id.empty() || !identities.insert(resource.id).second)
                throw std::runtime_error("Device identity is empty or duplicated");
            if (resource.kind == nks::ResourceKind::disk) {
                ++disks; if (resource.primary.valid()) ++validDisks;
                if (details) for (const auto& fact : resource.facts)
                    std::cout << utf8(resource.title + L"\t" + fact.name + L"\t" + fact.value + L"\n");
            }
            if (resource.kind == nks::ResourceKind::gpu) {
                ++gpus; if (resource.primary.valid()) ++validGpus;
                gpuEngines += static_cast<unsigned>(resource.engines.size());
                if (resource.secondary.valid() && resource.tertiary.valid()) ++validGpuMemory;
            }
            if (resource.kind == nks::ResourceKind::network) { ++networks; if (resource.status == L"Odpojeno") ++disconnected; }
            if (resource.status == L"Odpojeno" && resource.primary.valid())
                throw std::runtime_error("Disconnected network has a valid utilization");
        }
        if (after.measuredAt <= before.measuredAt)
            throw std::runtime_error("Device timestamp did not advance");
        std::cout << "{\"elevated\":" << (elevated() ? "true" : "false") << ",\"disks\":" << disks << ",\"validDisks\":" << validDisks
            << ",\"gpus\":" << gpus << ",\"validGpus\":" << validGpus
            << ",\"gpuEngines\":" << gpuEngines << ",\"validGpuMemory\":" << validGpuMemory
            << ",\"networks\":" << networks << ",\"disconnected\":" << disconnected
            << ",\"collectionMs\":" << std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - began).count() << "}\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
