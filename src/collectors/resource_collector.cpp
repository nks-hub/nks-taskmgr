#include <winsock2.h>
#include <ws2ipdef.h>
#include "resource_collector.hpp"
#include "gpu_telemetry.hpp"
#include "disk_telemetry.hpp"
#include <setupapi.h>
#include <winioctl.h>
#include <ntddstor.h>
#include <ntdddisk.h>
#include <iphlpapi.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <algorithm>
#include <memory>
#include <cwchar>
#include <iomanip>
#include <sstream>

namespace nks {
namespace {

Metric<double> find(const std::vector<NamedCounter>& values, const std::wstring& name)
{
    const auto match = std::find_if(values.begin(), values.end(), [&](const auto& item) { return item.name == name; });
    return match == values.end() ? Metric<double>{0, SampleState::unavailable, MetricSource::performanceCounter} : match->metric;
}

std::wstring luidName(LUID luid)
{
    wchar_t value[48]{};
    swprintf_s(value, L"luid_0x%08x_0x%08x", static_cast<unsigned>(luid.HighPart), luid.LowPart);
    return value;
}

std::wstring diskValue(const Metric<double>& metric, double multiplier, const wchar_t* unit)
{
    if (!metric.valid()) return L"Měření nedostupné";
    std::wostringstream text;
    text << std::fixed << std::setprecision(2) << metric.value * multiplier << unit;
    auto result = text.str();
    std::replace(result.begin(), result.end(), L'.', L',');
    return result;
}

}

void ResourceCollector::inventory()
{
    std::vector<ResourceRecord> next;
    std::map<std::wstring, std::uint32_t> nextNumbers;
    bool diskComplete = true;
    const auto set = SetupDiGetClassDevsW(&GUID_DEVINTERFACE_DISK, nullptr, nullptr, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (set != INVALID_HANDLE_VALUE) {
        for (DWORD index = 0;; ++index) {
            SP_DEVICE_INTERFACE_DATA entry{sizeof(entry)};
            if (!SetupDiEnumDeviceInterfaces(set, nullptr, &GUID_DEVINTERFACE_DISK, index, &entry)) {
                diskComplete = diskComplete && GetLastError() == ERROR_NO_MORE_ITEMS;
                break;
            }
            DWORD size = 0;
            SetupDiGetDeviceInterfaceDetailW(set, &entry, nullptr, 0, &size, nullptr);
            if (size < sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) || size > 64 * 1024) {
                diskComplete = false;
                continue;
            }
            std::vector<std::byte> data(size);
            auto* detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(data.data());
            detail->cbSize = sizeof(*detail);
            SP_DEVINFO_DATA info{sizeof(info)};
            if (!SetupDiGetDeviceInterfaceDetailW(set, &entry, detail, size, nullptr, &info)) {
                diskComplete = false;
                continue;
            }
            wchar_t name[1024]{};
            if (!SetupDiGetDeviceRegistryPropertyW(set, &info, SPDRP_FRIENDLYNAME, nullptr,
                reinterpret_cast<BYTE*>(name), sizeof(name), nullptr))
                wcscpy_s(name, L"Diskové zařízení");
            ResourceRecord disk;
            disk.kind = ResourceKind::disk;
            disk.id = L"disk:" + std::wstring(detail->DevicePath);
            disk.title = L"Disk";
            disk.description = name;
            disk.facts = diskHardware(detail->DevicePath);
            const auto handle = CreateFileW(detail->DevicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                nullptr, OPEN_EXISTING, 0, nullptr);
            if (handle != INVALID_HANDLE_VALUE) {
                STORAGE_DEVICE_NUMBER number{};
                DWORD used = 0;
                if (DeviceIoControl(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, nullptr, 0, &number, sizeof(number), &used, nullptr)
                    && used >= sizeof(number)) {
                    nextNumbers.emplace(disk.id, number.DeviceNumber);
                    disk.title = L"Disk " + std::to_wstring(number.DeviceNumber);
                }
                CloseHandle(handle);
            }
            next.push_back(std::move(disk));
        }
        SetupDiDestroyDeviceInfoList(set);
    } else diskComplete = false;
    if (!diskComplete) {
        next.clear();
        for (const auto& item : devices)
            if (item.kind == ResourceKind::disk) next.push_back(item);
        nextNumbers = diskNumbers;
    }
    std::map<std::wstring, std::wstring> nextLuids;
    std::map<std::wstring, LUID> nextLuidValues;
    Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
    bool gpuComplete = true;
    if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) {
        Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
        for (UINT index = 0;; ++index) {
            const auto status = factory->EnumAdapters1(index, &adapter);
            if (status != S_OK) { gpuComplete = gpuComplete && status == DXGI_ERROR_NOT_FOUND; break; }
            DXGI_ADAPTER_DESC1 description{};
            const auto described = adapter->GetDesc1(&description);
            if (FAILED(described)) gpuComplete = false;
            if (SUCCEEDED(described) && !(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)
                && !gpuDisplayAlias(description.AdapterLuid)) {
                ResourceRecord gpu;
                gpu.kind = ResourceKind::gpu;
                gpu.id = luidName(description.AdapterLuid);
                gpu.title = L"GPU " + std::to_wstring(index);
                gpu.description = description.Description;
                gpu.capacity = static_cast<double>(description.DedicatedVideoMemory);
                gpu.sharedCapacity = static_cast<double>(description.SharedSystemMemory);
                nextLuids.emplace(gpu.id, gpu.id);
                nextLuidValues.emplace(gpu.id, description.AdapterLuid);
                next.push_back(std::move(gpu));
            }
            adapter.Reset();
        }
    } else gpuComplete = false;
    if (!gpuComplete) {
        std::erase_if(next, [](const auto& item) { return item.kind == ResourceKind::gpu; });
        for (const auto& item : devices)
            if (item.kind == ResourceKind::gpu) next.push_back(item);
        nextLuids = gpuLuids;
        nextLuidValues = gpuLuidValues;
    }
    std::sort(next.begin(), next.end(), [&](const auto& a, const auto& b) {
        if (a.kind != b.kind) return a.kind < b.kind;
        if (a.kind == ResourceKind::disk) {
            const auto an = nextNumbers.contains(a.id) ? nextNumbers.at(a.id) : MAXDWORD;
            const auto bn = nextNumbers.contains(b.id) ? nextNumbers.at(b.id) : MAXDWORD;
            if (an != bn) return an < bn;
        }
        return a.id < b.id;
    });
    devices = std::move(next);
    diskNumbers = std::move(nextNumbers);
    std::erase_if(diskHealthFacts, [&](const auto& item) { return !diskNumbers.contains(item.first); });
    gpuLuids = std::move(nextLuids);
    gpuLuidValues = std::move(nextLuidValues);
    disksFresh = diskComplete;
    gpusFresh = gpuComplete;
    inventoryAt = std::chrono::steady_clock::now();
    telemetryAt = {};
}

void ResourceCollector::networks(ResourceSnapshot& snapshot)
{
    MIB_IF_TABLE2* raw = nullptr;
    if (GetIfTable2(&raw) != NO_ERROR) {
        for (auto item : lastNetworks) {
            item.primary.state = item.secondary.state = SampleState::unavailable;
            item.status = L"Měření nedostupné";
            snapshot.devices.push_back(std::move(item));
        }
        previousNetworks.clear();
        return;
    }
    std::unique_ptr<MIB_IF_TABLE2, decltype(&FreeMibTable)> table(raw, FreeMibTable);
    std::map<std::uint64_t, NetworkBefore> next;
    lastNetworks.clear();
    for (ULONG i = 0; i < raw->NumEntries; ++i) {
        const auto& row = raw->Table[i];
        if (!row.InterfaceAndOperStatusFlags.HardwareInterface || row.InterfaceAndOperStatusFlags.FilterInterface
            || row.Type == IF_TYPE_SOFTWARE_LOOPBACK)
            continue;
        ResourceRecord network;
        network.id = L"network:" + std::to_wstring(row.InterfaceLuid.Value);
        network.kind = ResourceKind::network;
        network.title = row.Alias;
        network.description = row.Description;
        network.capacity = static_cast<double>(std::max(row.TransmitLinkSpeed, row.ReceiveLinkSpeed));
        const bool connected = row.OperStatus == IfOperStatusUp;
        network.status = connected ? L"Připojeno" : L"Odpojeno";
        network.facts = {{L"Rychlost linky ↓", hardwareRate(row.ReceiveLinkSpeed)}, {L"Rychlost linky ↑", hardwareRate(row.TransmitLinkSpeed)},
            {L"MTU", std::to_wstring(row.Mtu) + L" B"}, {L"Připojení", row.Type == IF_TYPE_IEEE80211 ? L"Wi-Fi" : row.Type == IF_TYPE_ETHERNET_CSMACD ? L"Ethernet" : L"Jiné"}};
        network.primary = {0, connected ? SampleState::first : SampleState::unavailable, MetricSource::networkInterface};
        network.secondary = network.primary;
        const auto before = previousNetworks.find(row.InterfaceLuid.Value);
        if (connected && before != previousNetworks.end() && before->second.connected) {
            const auto seconds = std::chrono::duration<double>(snapshot.measuredAt - before->second.measuredAt).count();
            network.primary = networkRate(row.InOctets, before->second.received, seconds);
            network.secondary = networkRate(row.OutOctets, before->second.sent, seconds);
        }
        next.emplace(row.InterfaceLuid.Value, NetworkBefore{row.InOctets, row.OutOctets, connected, snapshot.measuredAt});
        lastNetworks.push_back(network);
        snapshot.devices.push_back(std::move(network));
    }
    previousNetworks = std::move(next);
}

ResourceSnapshot ResourceCollector::collect()
{
    const auto now = std::chrono::steady_clock::now();
    if (now - inventoryAt > std::chrono::seconds(15))
        inventory();
    const auto collected = counters.collect();
    ResourceSnapshot result;
    if (now - upsAt >= std::chrono::seconds(5)) { upsFacts = upsHardware(); upsAt = now; }
    result.upsFacts = upsFacts;
    result.measuredAt = std::chrono::steady_clock::now();
    result.diskCountersAvailable = counters.available(0);
    result.gpuCountersAvailable = counters.available(3);
    result.devices = devices;
    const auto idle = collected ? counters.read(0) : std::vector<NamedCounter>{};
    const auto reads = collected ? counters.read(1) : std::vector<NamedCounter>{};
    const auto writes = collected ? counters.read(2) : std::vector<NamedCounter>{};
    const auto engines = collected ? counters.read(3) : std::vector<NamedCounter>{};
    const auto dedicated = collected ? counters.read(4) : std::vector<NamedCounter>{};
    const auto shared = collected ? counters.read(5) : std::vector<NamedCounter>{};
    std::array<std::vector<NamedCounter>, 6> diskDetails;
    for (std::size_t i = 0; i < diskDetails.size(); ++i)
        if (collected) diskDetails[i] = counters.read(i + 6);
    const bool diskHealthDue = now - diskHealthAt >= std::chrono::seconds(30);
    const bool telemetryDue = now - telemetryAt >= std::chrono::seconds(5);
    for (auto& item : result.devices) {
        if ((item.kind == ResourceKind::disk && !disksFresh) || (item.kind == ResourceKind::gpu && !gpusFresh)) {
            item.primary.state = item.secondary.state = item.tertiary.state = SampleState::unavailable;
            item.status = L"Inventář není aktuální";
            item.temperature.state = SampleState::unavailable;
            continue;
        }
        if (item.kind == ResourceKind::gpu) {
            item.primary = busiestGpuEngine(engines, gpuLuids.at(item.id));
            item.engines = gpuEngines(engines, gpuLuids.at(item.id));
            item.secondary = gpuMemoryUsage(dedicated, item.id);
            item.tertiary = gpuMemoryUsage(shared, item.id);
            if (telemetryDue) {
                item.temperature = gpuTemperature(gpuLuidValues.at(item.id));
                const auto cached = std::find_if(devices.begin(), devices.end(), [&](const auto& device) { return device.id == item.id; });
                cached->temperature = item.temperature;
            }
            continue;
        }
        const auto number = diskNumbers.find(item.id);
        if (number == diskNumbers.end()) continue;
        if (diskHealthDue || !diskHealthFacts.contains(item.id))
            diskHealthFacts[item.id] = diskHealth(item.id.substr(5));
        const auto& healthFacts = diskHealthFacts.at(item.id);
        item.facts.insert(item.facts.end(), healthFacts.begin(), healthFacts.end());
        const auto prefix = std::to_wstring(number->second);
        std::wstring instance;
        const std::array<const std::vector<NamedCounter>*, 4> instances{&idle, &reads, &writes, &diskDetails[3]};
        for (const auto* values : instances) {
            const auto match = std::find_if(values->begin(), values->end(), [&](const auto& v) {
                return v.name == prefix || v.name.starts_with(prefix + L" ");
            });
            if (match != values->end()) { instance = match->name; break; }
        }
        if (!instance.empty()) item.title = L"Disk " + instance;
        item.primary = find(idle, instance);
        if (item.primary.valid()) item.primary.value = std::clamp(100 - item.primary.value, 0.0, 100.0);
        item.secondary = find(reads, instance);
        item.tertiary = find(writes, instance);
        constexpr std::array labels{L"Průměrná odezva", L"Odezva čtení", L"Odezva zápisu", L"Fronta požadavků", L"Čtení IOPS", L"Zápis IOPS"};
        for (std::size_t i = 0; i < diskDetails.size(); ++i)
            item.facts.push_back({labels[i], diskValue(find(diskDetails[i], instance), i < 3 ? 1000 : 1, i < 3 ? L" ms" : i > 3 ? L" operací/s" : L" požadavků")});
        item.capacity = 100;
    }
    networks(result);
    if (telemetryDue) telemetryAt = std::chrono::steady_clock::now();
    if (diskHealthDue) diskHealthAt = std::chrono::steady_clock::now();
    return result;
}

}
